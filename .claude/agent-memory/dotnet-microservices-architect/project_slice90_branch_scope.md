---
name: slice90-branch-scope
description: Issue #90 (epic #80 Phase C) branch/department membership SCOPE — a DISPLAY attribute, not an access boundary; how it mirrors module-licensing.
metadata:
  type: project
---

Issue #90 added branch/department membership scope as an ORGANIZATIONAL DISPLAY attribute — explicitly NOT an access-enforcement boundary (mirrors `platform.tenant_module_entitlements` module-licensing "DISPLAY GATE" pattern in 11_rbac_hardening.sql).

**Why:** the People UI needs to show/filter staff by "Cardiology · Andheri W" and a "N branches" stat, but a scoped user must keep IDENTICAL effective permissions. Permission resolution (`resolve_user_permissions`/`user_has_permission`/`get_user_menus`/`v_user_permissions`) NEVER reads `branch_id`/`department`.

**How to apply — key conventions if extending this area:**
- Schema (appended to `database/11_rbac_hardening.sql`, before POST-CONDITIONS; bundle regenerated via `database/regenerate_bundle.py`): `platform.branches` (RLS `rls_can_see/write_tenant`) + additive nullable `user_tenant_roles.branch_id`/`department`.
- **branches create = DIRECT own-tenant EF insert under RLS** (no SECURITY DEFINER) — branches confer no permissions so there's no R3 escalation surface; `GRANT SELECT,INSERT,UPDATE ... TO docslot_app`. This is the ONLY sanctioned direct-write to a platform table by the app (all RBAC tables still go through definer fns).
- **Scope setter = `platform.set_membership_scope(actor, user_tenant_role_id, branch_id, department)` SECURITY DEFINER** — because `user_tenant_roles` is an RBAC table, it re-checks actor `tenant.users.update` (or super_admin) and writes ONLY branch_id/department, NEVER role_id (no escalation). SQLSTATE map in `MembershipScopeWriter`: 42501/P0002→403, 23503/23000→409 (branch not in tenant).
- The endpoint is by `userId` (`PUT /api/v1/tenants/{tenantId}/users/{userId}/scope`) but the setter takes a `user_tenant_role_id`; the app resolves the target's **scope-bearing membership** = primary DESC, then earliest granted, then id. `MembershipScopeWriter.FindScopeMembershipAsync` and `UserDirectory` display MUST use this SAME ordering so the list reflects the exact row written.
- `UserListItemDto` extended with trailing optional params `BranchId/BranchName/Department` (positional record; keep them last so existing construction stays non-breaking).
- Endpoints on `AdminController`: `GET tenants/{id}/branches` (tenant.users.read), `POST tenants/{id}/branches` (tenant.settings.update), `PUT .../users/{userId}/scope` (tenant.users.update).
- Regression invariant test lives in `BranchScopeTests`: capture `resolve_user_permissions` before/after set-scope and assert identical. Relates to [[slice08_rbac_navigation]].
