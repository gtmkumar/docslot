---
name: user-management-lifecycle
description: User lifecycle slice (deactivate/reactivate, edit-profile, reset-access) + invite escalation-by-proxy fix — audited 2026-06-28, now CLEARED PASS (all 4 findings fixed & re-verified)
metadata:
  type: project
---

User-management lifecycle wave on branch feat/iam-roles-permissions-admin. Audited 2026-06-28. **VERDICT: PASS** — all 4 findings fixed and independently re-verified 2026-06-28 (not on coordinator say-so). Original verdict was PASS WITH CONDITIONS; conditions now cleared.

## Re-verification (2026-06-28) — all 4 fixes confirmed in working tree + live
- F1 (marker collision) CLOSED: marker is reserved `'[deactivated] '` (set_tenant_user_active 1493/1534, reactivate matches LIKE 1544/1555). revoke_role_assignment rejects `btrim(p_reason) ILIKE '[deactivated]%'` (782-784). LIVE-tested on fresh bundle: forged markers rejected across all evasions (exact, no-space, leading-space via btrim, UPPERCASE via ILIKE); legit revoke allowed & stored WITHOUT marker; a normally-revoked assignment is NOT resurrected by a later reactivate (active count stayed 0). Reject is correctly broader (no-space) than the marker (with-space).
- F2 (reactivate platform-scope parity) CLOSED: non-super branch now has `v_row.role_scope <> 'platform'` (1561) via `r.scope AS role_scope` (1549). Present in both 11_ and bundle.
- F3 (inert Password) CLOSED: `Password` removed from CreateUserRequest (AdminDtos.cs:24) and CreateUserRequestSchema (contracts.ts:391-397). Remaining `password` is LoginRequestSchema (legit). .NET builds 0 errors.
- F4 (stale comment) CLOSED: IUserLifecycle doc now "no_data_found (P0002) → 403 (avoids membership-enumeration oracle)".
- Bundle parity (md5) OK for the changed fns; fresh `psql -f docslot_complete.sql` = 0 ERROR lines.

## What shipped
4 new SECURITY DEFINER fns in `database/11_rbac_hardening.sql` (mirrored byte-identical into docslot_complete.sql — parity verified by md5):
- `tenant_has_other_active_admin(tenant, excluding_user)` (1442) — permission-based admin test (holds tenant.users.update OR tenant.roles.assign), never role_key. Used by both last-admin guards.
- `set_tenant_user_active(actor, target, tenant, is_active, reason)` (1472) — deactivate=soft-revoke all active memberships in THIS tenant marked `revoked_reason LIKE 'deactivated: %'`; reactivate=restore ONLY marked rows, re-running inline no-escalation guard per role. Self-guard (no self-deactivate, line ~1510, RAISEs "your own account"). Last-admin guard on deactivate (~1514).
- `update_user_profile(actor, target, tenant, full_name, phone, lang)` (1578) — whitelists full_name/phone/preferred_language ONLY. Verified LIVE: email/password_hash/is_active untouched.
- `reset_user_access(actor, target, tenant, reason)` (1626) — flags only: must_change_password=true, locked_until=NULL, failed_login_count=0. Verified LIVE: password_hash NOT nulled (chk_user_has_auth = `password_hash IS NOT NULL OR sso_provider IS NOT NULL`, 01:386), password_changed_at NOT advanced, self-guard fires.
AMENDED `revoke_role_assignment` (760): added permission-based last-admin guard (~802-820), ERRCODE integrity_constraint_violation (23000→409).
RELOCATED rls_can_see_tenant/rls_can_write_tenant to 1188/1205 (before MODULE LICENSING) to fix fresh-build ordering. Bodies byte-identical (md5 parity), CREATE OR REPLACE, defined exactly once each. Fresh `psql -f docslot_complete.sql` = exit 0, zero ERROR lines.

## Guards verified (all 13 from the brief HOLD)
- Actor ALWAYS ctx.UserId in every handler (UserAdmin.cs 62/104/143/186/222/257). Request.UserId is the TARGET, never the actor.
- Atomicity (guard #8): UnitOfWorkBehavior (Behaviors.cs ~165) opens ONE tx for the command, commits once at end. CreateUserCommandHandler does provisioning.CreateAsync (raw user INSERT on PlatformDbContext) THEN roles.AssignRoleAsync (definer) in the SAME ambient tx → a 403 on assign throws before CommitAsync → TenantScope.DisposeAsync rolls back the user INSERT (no orphan auth-less user). Initial-role routes through assign_role_to_user (no-escalation+SoD). UserProvisioning no longer does any role INSERT.
- Tenant scope (guard #4): deactivate filters tenant_id=p_tenant_id (concrete). Verified LIVE: deactivate in tenant A left global users.is_active=t AND admin1's membership in tenant B active. NEVER flips global is_active. Platform-scoped (tenant_id IS NULL) super_admin assignments are unreachable by these tenant-scoped fns.
- SQLSTATE map (guard #12, UserLifecycle.cs): 42501→403, 23000/23505→409, P0002→403. VERIFIED on live PG16: `no_data_found`→P0002, `integrity_constraint_violation`→23000, `insufficient_privilege`→42501. (Interface doc-comment wrongly says "no_data_found→404" but CODE maps P0002→ForbiddenException 403 = correct, prevents membership-enumeration oracle.)
- PHI/DPDP (guard #11): UserDirectory masks phone server-side via PhoneMasker.Mask (ReadHelpers.cs, keeps prefix+last2). UserListItemDto carries NO last_login_ip (INET) — only LastLoginAt. Audit ChangeSummary logs phone CHANGE by field name only ("name, phone, language"), never the value.
- Invite password (guard #13): provisioner ignores CreateUserRequest.Password, generates CSPRNG temp secret, hashes it, never returns/logs it; must_change_password=true. No result DTO carries a credential.
- Last-admin guards permission-based (not role_key): rely on v_user_permissions which filters revoked_at IS NULL + expires + u.is_active + tenant_is_serviceable — so a deactivated admin instantly stops counting. Structurally, for DEACTIVATE the non-self actor always remains an admin → guard is defense-in-depth.

## Non-blocking conditions / follow-ups
- MEDIUM (correctness, not escalation): reactivate matches the literal `revoked_reason LIKE 'deactivated: %'` marker; `revoke_role_assignment` writes `left(p_reason,200)` with NO marker sanitization. An admin who crafts a normal revoke reason starting "deactivated: " could have that role silently un-revoked by a later unrelated reactivate. NOT an escalation (reactivate re-runs the no-escalation guard per role) — at worst a revoke-for-cause is undone, an audit-integrity footgun. Fix: dedicated boolean/column for the deactivation marker, or sanitize/reject the prefix in revoke_role_assignment.
- LOW: reactivate inline escalation guard (1550-1559) omits the explicit `scope='platform'` check that assign_role_to_user has. Not exploitable (platform roles have NULL tenant_id, unreachable here; and the no-escalation perm check would block restore anyway) — add for parity.
- LOW/NOTE: CreateUserRequest.Password field is inert (ignored) — remove from DTO to avoid implying it sets a password.
- LOW: IUserLifecycle XML doc says "no_data_found → 404"; code is 403. Fix the comment.

See [[backend-iam-roles-admin]], [[rbac-catalog-plane]], [[backend-rbac-super-admin-guc]], [[backend-slice08-rbac-navigation]].
