---
name: backend-issue91-tenant-security-policy
description: Tenant security-policy subsystem (#91) — 2FA/login-hours/IP/password/masking; the login-gate-only-not-refresh/switch bypass BLOCKER
metadata:
  type: project
---

Issue #91 (C3, epic #80): tenant security policy stored in `platform.tenants.settings->'security'` JSONB (NO new table — deliberate). Reads merge over `SecurityPolicy.Default` (all gates OFF, masking ON, minLen 8) so unconfigured tenants behave as before.

**Enforcement points (all REAL at their point, proven by SecurityPolicyTests):**
- `mediq.Infrastructure/Security/LoginSecurityPolicyGate.cs` — invoked ONLY from `LoginCommandHandler.Handle` after password+tenant, before token. Order: IP allow-list (fail-closed) → login-hours (IST +05:30 fixed, doctor exempt via `docslot.doctor.update_self`) → MFA tier (owners_admins = holds `tenant.users.update` OR `tenant.roles.assign`; all = everyone). MFA throws `MfaEnrollmentRequiredException` → 403 code `mfa_enrollment_required`; hours/IP throw ForbiddenException 403.
- Password min-length: `ChangePassword/ChangePasswordCommand.cs` handler (tenant-policy-aware, 422). Invitation-accept still static MinimumLength(8) — disclosed deferred.
- Masking: `PatientDetail.cs` GetPatientDetailQueryHandler — `maskPhone = toggle && !permissions.Has(docslot.medical_history.read)` → PhoneMasker. **Phone only** (email/DOB/age still returned in full to front-desk regardless of toggle — matches pre-existing phone-only path but toggle name implies broader).
- IP source = `Connection.RemoteIpAddress` (RequestContext.cs:49), NOT a client header. X-Test-Ip is a test-only IStartupFilter. Not spoofable (X-Forwarded-For strict default-deny).

**BLOCKER (requirement 3) — RESOLVED, re-audited 2026-07-01, now PASS.** The login-only gate now binds both token-reissue paths:
- `SwitchTenantCommand.cs:65` — `policyGate.EnforceAsync(user, req.TenantId /*TARGET*/, requestContext.IpAddress, now, ct)` runs after the membership check, before minting (line 79). Violation audits `switch_tenant_denied` + rethrows (403 mfa_enrollment_required / forbidden), leaves current session untouched. Switching INTO a hardened tenant now enforces its MFA/hours/IP.
- `RefreshCommand.cs:62` — `policyGate.EnforceAsync(user, session.ActiveTenantId, ...)` runs before minting (line 75); full gate (hours+IP+MFA) so a policy tightened AFTER login binds the existing session on its next rotation. Violation audits `refresh_denied` + rethrows.
- Tests: `SwitchTenantRefreshPolicyTests` — 8/8 pass live. Genuine negatives WITH positive controls: no-mfa blocked under mfaPolicy=all vs mfa-enrolled succeeds; out-of-hours blocked (window 2h ahead) vs unhardened succeeds; non-allowlisted IP blocked; refresh blocked out-of-hours / blocked-IP when tightened-after-login vs compliant renews.
- Impersonation (`ImpersonationCommands.cs`) also mints a token but is OUT OF SCOPE: super_admin-gated (`platform.users.impersonate`), fully audited, keeps the actor's OWN active tenant and only ADDS an `impersonated_tenant` claim — not a tenant-member evading their own tenant's controls. Not a #91 bypass.

Copy honesty (SecurityTab.tsx + i18n.ts, verified 2026-07-01): receptionist mask = phone-only ("Mask patient phone number...", "Email and date of birth stay visible"); idle-timeout carries "Not yet enforced" badge; MFA-required carries "(enrolment flow pending)" enrolNote. All honest.

**Honestly deferred (verified not faked):** device-verification email send (gate ignores `requireNewDeviceVerification`); `idleTimeoutMinutes` stored+validated but not enforced (belongs to session layer); real TOTP enrolment/challenge flow not built (mfa_enabled is just a flag → enabling a required tier locks out covered staff since no enrolment endpoint exists; StaffPendingMfaEnrolment count surfaces this).

Tenant scoping SOLID: policy tenant from signed JWT (`RequireTenant()`), all SQL scoped by tenant_id, cross-tenant isolation test present, jsonb_set touches only 'security' key. No new grant introduced (docslot_app already had UPDATE on platform.tenants). Perms: read=tenant.settings.read, update=tenant.settings.update, ip=platform.ip_allowlist.manage.
