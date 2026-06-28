---
name: backend-iam-roles-admin
description: IAM Roles&permissions admin slice — CLEARED PASS 2026-06-26; duplicate_role grant-option escalation (HIGH) fixed & verified; 2 low-sev follow-ups open
metadata:
  type: project
---

IAM "Team & roles" admin slice 1 (RBAC write paths). Audited + CLEARED 2026-06-26.

**STATUS: PASS.** Finding 1 fix applied & verified in BOTH `database/11_rbac_hardening.sql:1031-1035` and regenerated `database/docslot_complete.sql:7329-7331`: copy uses `CASE WHEN platform.is_super_admin(p_actor_user_id) THEN rp.is_grantable ELSE false END`. Coordinator runtime check: 74/74 is_grantable=false for non-super tenant_owner dup, 74/74 preserved for super_admin dup. Cleared on my own re-read of source, not coordinator say-so. Escalation invariant now uniform across grant/revoke/duplicate.

**Two new SECURITY DEFINER funcs in `database/11_rbac_hardening.sql`:**
- `revoke_permission_from_role(actor, role_id, perm_id, tenant DEFAULT NULL)` RETURNS BOOLEAN — sound. Mirrors grant guard (non-super: not platform-scope, must hold perm with `is_grantable=true`) AND adds system-role lock (non-super may not edit `is_system` role matrix). Idempotent DELETE.
- `duplicate_role(actor, src_role, key, name, desc, tenant)` RETURNS UUID — **HIGH escalation hole**. No-escalation check (lines ~1011-1021) verifies actor *holds* each source perm but copy (lines ~1028-1031) preserves `rp.is_grantable` verbatim. `is_grantable` DEFAULTs true (line 630), so non-super admin holding perm X *without* grant option can duplicate a built-in role → new tenant role where X is grantable → then becomes a grant-option delegation source they couldn't create directly. `assign_role_to_user` does NOT share this (it assigns existing roles, doesn't mint grant-option sources).

**Condition (HONORED 2026-06-26):** non-super dup forces `is_grantable=false` on the copy. Done exactly as specified + bundle regenerated.

**Cleared / verified:**
- API gate split is defensible: writes=`tenant.roles.assign`, reads=`tenant.users.read`; DB definer funcs are the real boundary (`IamController.cs`). super_admin holds all; tenant_owner holds tenant.roles.assign.
- Actor is always `ctx.UserId`, never request body (`IamFeatures.cs`). Good.
- App-layer audit (IAuditTrailWriter) on grant/revoke/duplicate is ACCEPTABLE here — unlike begin_impersonation these aren't standing cross-tenant capabilities and have a single sanctioned caller. Audit row in same UoW tx → rolls back with a failed mutation. revoke only audits when didRevoke=true (fine).
- Reads safe under R1 RLS: `IamReadService` runs in request tx; roles/role_permissions gated by rls_can_see_tenant (own+global+super/impersonation); permissions/resource_types/action_types are global catalog. effective-access → resolve_user_permissions (definer, R2-gated). `GetEffectiveAccessQueryHandler` defaults null tenant to ctx.TenantId (good).

**Low-sev follow-ups (non-blocking):**
- `RoleAssignmentRepository.cs` 403/409 pass `pg.MessageText` through → leaks actor/role/perm UUIDs to the (already-authorized) caller. Non-secret IDs; tighten to a static client msg, keep MessageText server-side only.
- effective-access endpoint accepts arbitrary `?tenantId=`; resolution for a tenant the caller isn't a member of is only gated by tenant.users.read in caller's own tenant. Low risk (perm keys not PHI, foreign userIds not enumerable). Consider validating tenantId ∈ caller memberships.

Repo wrappers: GrantPermissionToRoleAsync (ExecAsync/void), Revoke/DuplicateRoleAsync (ScalarAsync); SQLSTATE 42501→403 (ForbiddenException), 23000→409 (ConflictException). EXECUTE granted to docslot_app in the explicit block (lines ~1194-1196). See [[backend-slice08-rbac-navigation]], [[backend-rbac-super-admin-guc]], [[backend-issue3-impersonation-wiring]].
