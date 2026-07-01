---
name: rbac-last-admin-guard-test-pattern
description: How to actually observe the last-admin-guard 409 (set_tenant_user_active / revoke_role_assignment) in a test — super_admin does NOT work, and why.
metadata:
  type: reference
---

Issue #79 gap: `platform.tenant_has_other_active_admin` (`database/11_rbac_hardening.sql:1448`) blocks
stripping a tenant's FINAL admin. It's enforced inside `set_tenant_user_active` (`:1478`, guard at
`:1522-1530`) and `revoke_role_assignment` (`:760`, guard at `:806-826`). Before issue #79 only the ALLOWED
path was tested (`UserLifecycleTests.Admin_DeactivateOtherAdmin_AllowedWhileAnotherRemains`); the BLOCKING
branch was added in `NonMemberActor_GlobalOverride_*` tests in the same file.

**Counter-intuitive finding: a genuine platform `super_admin` actor CANNOT be used to observe this 409.**
Both guard functions gate their ENTIRE last-admin check behind `NOT platform.is_super_admin(p_actor_user_id)`
— so a real super_admin bypasses the guard outright and the call SUCCEEDS (200), never hitting the 409. This
contradicts the intuitive assumption that "a platform-scoped actor with no tenant membership" ⇒ "use
super_admin". `RbacSuperAdminGucWebAppFactory`'s super-admin fixture is ALSO not directly reusable here for
this reason (it exists to prove `app.is_super_admin` GUC / RLS cross-tenant visibility, a different guard
entirely — see `platform.rls_can_see_tenant`, not `tenant_has_other_active_admin`).

**The actor shape that DOES work**: a user with ZERO `platform.user_tenant_roles` rows anywhere in the
system, empowered only by a GLOBAL grant-override — `platform.user_permission_overrides` row with
`tenant_id IS NULL`, `is_allowed = true`, for `tenant.users.update` (deactivate path) and/or
`tenant.roles.assign` (revoke path). Why this specific shape:
- `user_has_permission`/`resolve_user_permissions` treat an override with `tenant_id IS NULL` as matching
  ANY `p_tenant_id` (the `OR o.tenant_id IS NULL` clause) — so the actor passes both the `[RequirePermission]`
  gate at login-resolved tenant (which for a zero-membership user is `NULL`, since JWT `tenant_id` claim is
  set ONLY from `LoginCommandHandler.ResolveActiveTenantAsync`'s validated-membership check — no
  `X-Tenant-Id` header fallback, see `CurrentUserContext.TenantId`) AND the SECURITY DEFINER function's own
  defense-in-depth re-check using the REAL route tenantId.
- Note `tenant.users.update`/`tenant.roles.assign` are permission-SCOPE `'tenant'`, not `'platform'` — so the
  `OR scope='platform'` branch in `user_has_permission` does NOT help here; only a NULL-tenant override
  bridges the gap. A platform-scoped ROLE assignment (`user_tenant_roles.tenant_id = NULL`) would NOT work
  either for these specific permissions, for the same reason.
- Crucially, this actor has NO `user_tenant_roles` row for the target tenant, so
  `tenant_has_other_active_admin(target_tenant, target_user)` — which scans `user_tenant_roles` membership
  rows only, excluding just the target — never counts the actor as "another admin". That's the missing
  ingredient: any actor who IS a member with the admin permission would itself count as the "other admin"
  and the guard would never fire.

**Pinning the 409 to THIS guard (not a coincidental SoD/dup 23xxx)**: `UserLifecycle.ExecAsync` /
`RoleAssignmentRepository`'s catch clauses wrap `PostgresException.MessageText` VERBATIM into
`ConflictException(pg.MessageText)` (unlike the separate `DbUpdateException` path in
`ExceptionHandler.ClassifyDbUpdate`, which redacts). So the raw RAISE EXCEPTION text reaches the HTTP body
and tests should assert on it, not just the 409 status:
- deactivate path: `"cannot deactivate the last administrator of tenant %"`
- revoke path: `"cannot revoke the last administrator''s access in tenant %"` (renders as `...administrator's
  access in tenant <uuid>`)
- SoD trigger's text is completely different (`"SoD violation: role % is incompatible with already-held role
  % for user % in this tenant"`), so a `Contains("last administrator")` substring check safely discriminates.

Test location: `backend/mediq/tests/mediq.IntegrationTests/UserLifecycleTests.cs` —
`NonMemberActor_GlobalOverride_Deactivate_SoleAdmin_Returns409_LastAdminGuard`,
`NonMemberActor_GlobalOverride_Revoke_SoleAdminAssignment_Returns409_LastAdminGuard`, and a positive control
`NonMemberActor_GlobalOverride_Deactivate_Permitted_WhenCustomRoleHolderCountsAsAdmin` (proves the guard is
permission-based: a second member with a custom, non-admin-named role holding only `tenant.users.update`
counts as "another admin" and the deactivate then succeeds). Each test seeds its OWN throwaway tenant/users
via the owner connection (mirrors `UserLifecycleWebAppFactory`'s pattern) and cleans up in a `finally` —
none of it touches the class's shared `AdminEmail`/`Admin2Email`/`MemberEmail` fixture state, so it's safe
regardless of xUnit's within-class test ordering.

Reusable helper: `IamAdminWebAppFactory.PermissionIdAsync(string permissionKey)` and
`.SystemRoleIdAsync(string roleKey)` are `static` and already used cross-fixture (e.g. from
`UserLifecycleTests`) to resolve permission_id/role_id by key without needing a new fixture instance.

See also [[integration-test-harness]] for the general live-DB fixture conventions this pattern builds on.
