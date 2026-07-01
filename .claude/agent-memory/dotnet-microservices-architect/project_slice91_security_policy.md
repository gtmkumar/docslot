---
name: slice91-security-policy
description: Issue #91 tenant security-policy subsystem — storage in tenants.settings JSONB, real login/password/masking enforcement, and the schema gotchas that bit during the build
metadata:
  type: project
---

Issue #91 (Phase C of epic #80): tenant SECURITY-POLICY subsystem. Backend built, no new table.

**Storage** — policy lives in `platform.tenants.settings->'security'` JSONB (01_platform_core.sql). No DDL added. Absent keys merge over `SecurityPolicy.Default` (all gates OFF, masking ON, minLen 8). IP allow-list REUSES `platform.ip_allowlist` (05); receptionist masking REUSES the always-mask patient-detail path (gated on the toggle now).

**Endpoints** (route `api/v1/security`, tenant from JWT): GET/PUT `/policy` (gated `tenant.settings.read`/`.update`); GET/POST/DELETE `/ip-allowlist` (gated `platform.ip_allowlist.manage`). DELETE is soft-deactivate (docslot_app has no DELETE grant on platform tables). Plus new `POST /api/v1/auth/change-password` (tenant-policy min-length enforced → 422).

**Enforcement points (all REAL, tested to BLOCK):**
- 2FA tier → `LoginSecurityPolicyGate` (Infrastructure/Security), called in `LoginCommandHandler` AFTER password+tenant, BEFORE token. Distinct outcome `MfaEnrollmentRequiredException` (Utilities) → 403 code `mfa_enrollment_required` (added a Classify arm in ExceptionHandler).
- Login hours (IST +05:30 fixed) + IP allow-list → same gate.
- Password min → `ChangePasswordCommandHandler`.
- Masking → `GetPatientDetailQueryHandler` computes `maskPhone = policy.MaskSensitiveForReceptionist && !permissions.Has("docslot.medical_history.read")`; `IPatientReadService.GetDetailAsync` took a new `bool maskPhone`.
- Device-verify toggle: stored only; email send is credential-gated (#93) → deferred.

**GOTCHAs (cost real debug time):**
1. EF `ExecuteSqlRaw`/`SqlQueryRaw` parse `{...}` as positional placeholders → a jsonb path literal `'{security}'` or empty `'{}'::jsonb` throws `FormatException: ...Expected an ASCII digit`. Use `jsonb_set(COALESCE(settings, jsonb_build_object()), ARRAY['security'], CAST(@p AS jsonb), true)` — NO brace literals. (Raw Npgsql in test fixtures is fine — only EF's parser does this.)
2. `docslot.doctor.read_self` DOES NOT EXIST — 03_docslot.sql lists it in the doctor role's grant IN-list but it's silently not granted (only `docslot.doctor.update_self` exists). Used `docslot.doctor.update_self` as the doctor signal (held by doctor role, NOT tenant_staff).
3. `tenant_owner` does NOT hold `platform.ip_allowlist.manage`: it's seeded in 05 AFTER 01's tenant_owner all-tenant-perms sweep, and only super_admin gets the 05 `platform.%` re-sweep. Tests grant it to the owner via a `user_permission_overrides` grant (is_allowed=true).
4. `platform.tenants` / `ip_allowlist` / `access_policies` have NO RLS → scope every query by tenant_id explicitly. `role_permissions`/`user_tenant_roles` DO have RLS (11) — the pending-MFA count query works under docslot_app because it runs in the tenant-scoped read tx.
5. TestServer `HttpContext.Connection.RemoteIpAddress` is null → IP gate fails closed. Test fixture injects an `IStartupFilter` middleware that sets RemoteIpAddress from an `X-Test-Ip` header for deterministic block/allow.

Tier keys: owners_admins = holds `tenant.users.update` OR `tenant.roles.assign`; masking-exempt = `docslot.medical_history.read`. See `SecurityPolicyPermissions`.

**#91 auditor-BLOCKER follow-up (reissue-path bypass):** `LoginSecurityPolicyGate.EnforceAsync` was originally called ONLY from `LoginCommandHandler`. The two OTHER token-minting paths were bypasses — now ALSO gated: `SwitchTenantCommandHandler` enforces against the TARGET tenant (req.TenantId) after the membership check, before minting; `RefreshCommandHandler` re-enforces against `session.ActiveTenantId` before minting so a policy TIGHTENED after login binds the existing session on next rotation. Same 403 outcomes; gate reused as-is (its checks — IP/hours/MFA-coverage — are all time/context-sensitive and appropriate on renewal, no skip flag needed). `resolve_user_permissions` is SECURITY DEFINER (bypasses RLS, filters by tenant arg) so the gate resolves the target-tenant perms regardless of the pipeline's own tenant scope. Test gotcha: `switch-tenant` is `[Authorize]` → the reissue test must attach the HOME access-token bearer AND the refresh token in the body. Tests: `SwitchTenantRefreshPolicyTests` (own dual-tenant multi-membership fixture — the single-tenant SecurityPolicy cast can't express switch).
