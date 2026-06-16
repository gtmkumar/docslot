---
name: backend-slice01-platform-core
description: Slice 01 .NET platform_core backend — auth/RBAC/tenant/audit security facts; header-trust blocker + refresh-reuse RESOLVED & cleared (PASS 2026-06-14)
metadata:
  type: project
---

`backend/mediq/` Slice 01 (platform identity/auth/RBAC/tenant/audit). Audited 2026-06-14.

**Architecture that is CORRECT (re-verify, don't re-derive):**
- RBAC resolved ONCE/request in `mediq.Api/Authorization/PermissionResolutionMiddleware.cs` → cached on `PermissionContext` (`RequestContext.cs`); `RequirePermissionAttribute`/`PermissionAuthorizationHandler` check in-memory. Deny-wins is applied ENTIRELY in SQL (`platform.resolve_user_permissions`, `08_rbac_navigation.sql`); C# never re-derives it (`RbacQueryService.cs` only marshals via FromSqlRaw). No hardcoded role-name checks in C#.
- `perm:<key>` policies are materialized dynamically (`PermissionPolicyProvider`). All keys used by `AdminController` exist in seed (`01_platform_core.sql:212-241` + `08_rbac_navigation.sql:471-472` for `platform.overrides.*`). No phantom keys.
- Passwords: `PasswordHasher.cs` verifies bcrypt (pgcrypto seeds) + argon2id, new hashes argon2id (OWASP params), fail-closed on unknown format, FixedTimeEquals. Login (`LoginCommandHandler.cs`) has uniform `InvalidCredentialsException` for unknown-email vs bad-password (no enumeration), lockout via `login_attempts` + `RegisterFailedLogin`.
- Sessions/refresh (`SessionStore.cs`): only SHA-256 HASHES stored; rotation revokes-old/writes-new; logout revokes by access-hash; refresh checks revoked+expiry. Refresh reuses tenant from SESSION not header (good).
- JWT validation PARITY: Gateway (`mediq.Gateway/Program.cs`) and API (`ApiServiceExtensions.cs`) both validate issuer/audience/signing-key/lifetime, 30s skew, same HMAC key. Claims: sub/email/jti/tenant_id.
- Role create forces `IsSystem=false` (`Domain/Platform/Rbac.cs:41` CreateCustom); revoke is soft + reason mandatory (validator). Audit writer (`AuditTrailWriter.cs`) INSERT-only, never computes chain hash (DB `trg_audit_chain` does), never deletes.
- `users.mfa_secret` is in `encrypted_fields_registry` (basis=contract) and is UNMAPPED in EF (`PlatformConfigurations.cs:26`) — MFA storage deferred, no plaintext leak. Only `mfa_enabled` flag is read.
- Idempotency behavior present; store is `InMemoryIdempotencyStore` (durable table deferred to slice 03 — acceptable now, MUST land before money/booking endpoints).
- RLS (`relrowsecurity=true`) is live ONLY on docslot PHI tables: prescriptions, lab_reports, abdm_health_records, patient_medical_history, drug_alerts. NO platform.* table has RLS.
- 5/5 integration tests pass against live `docslot_platform`. The resolve-once test genuinely asserts `ResolveCallCount==1`.

**THE BLOCKER — RESOLVED & re-verified 2026-06-14 (cleared, PASS).** `RequestContext.cs:40-42` `CurrentUserContext.TenantId` now derives the active tenant ONLY from the validated JWT `tenant_id` claim — no `X-Tenant-Id` header code path exists anywhere (grep: only doc-comments mention it). The single value still flows into `resolve_user_permissions` (`PermissionResolutionMiddleware.cs:21`), `set_config('app.tenant_id')` (`UnitOfWork.cs:23` via `Behaviors.cs:101`), and the idempotency partition — so closing the header fixed all paths. Tenant switch is now `POST /api/v1/auth/switch-tenant` (`[Authorize]`, `SwitchTenantCommand.cs`): fail-closed server-side membership check (`memberships.All(m => m.TenantId != req.TenantId)` → 403 + `switch_tenant_denied` audit) before minting a new token via `RotateRefreshWithTenantAsync`. Test `Spoofed_XTenantId_Header_Is_Ignored_*` proves header is ignored (active tenant + perm set unchanged); `SwitchTenant_To_NonMember_Tenant_Is_Forbidden` proves 403.

**Refresh-reuse — RESOLVED & re-verified 2026-06-14.** `RefreshCommand.cs` now uses `FindByRefreshHashIncludingRevokedAsync` so a revoked token is FOUND; reuse-after-revoke → `RevokeAllForUserAsync` (whole-chain revoke) + `refresh_reuse_detected` audit → 401. Rotation is CHAINED (`RevokeAsync("rotated")` + new `CreateAsync`), not in-place, so replays remain detectable. Test `Refresh_Reuse_After_Rotation_Revokes_Whole_Chain` proves the rotated token ALSO dies after reuse. NOTE: SwitchTenant rotates the session IN PLACE (`RotateRefreshWithTenantAsync`) rather than chaining — minor, it's an authenticated+membership-validated action; the primary refresh path chains correctly. 8/8 integration tests pass.

**Prod-hardening conditions (tracked, NOT Slice-01 blockers — still appropriately deferred):** HMAC-SHA256 with a committed dev key (`appsettings.json` SigningKey, labeled dev) → RS256/JWKS + secret from env/GHA. Audit append-only enforced only in app code — DB role `gtmkumar` still has UPDATE/DELETE on `platform.audit_log`; add a DB-level guard → slice 05. Durable idempotency table → slice 03 (before money/booking). AppHost pulls MessagePack 2.5.192 (GHSA-hv8m-jj95-wg3x) — bump.
