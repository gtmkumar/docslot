---
name: backend-issue90-branch-scope
description: Issue #90 branch/department membership scope — DISPLAY-ONLY org attribute on user_tenant_roles + platform.branches; cleared PASS
metadata:
  type: project
---

Issue #90 (epic #80 Phase C): branch/department membership SCOPE. CLEARED — clean PASS, no required changes.

**What shipped** (database/11_rbac_hardening.sql ~2036-2144, mirrored in docslot_complete.sql ~9651-9742):
- New `platform.branches` (TABLE #... under platform): tenant_id NOT NULL FK→tenants ON DELETE CASCADE, soft-delete, RLS ENABLED with the SHARED helpers `rls_can_see_tenant`/`rls_can_write_tenant` (read=SELECT, write=ALL WITH CHECK). Grant is SELECT,INSERT,UPDATE to docslot_app (no DELETE — soft-delete only). Direct own-tenant write under RLS is intentional and safe because **branches confer no permissions → no R3 escalation surface** (no SECURITY DEFINER needed, unlike RBAC assign/grant tables).
- Two nullable additive cols on `platform.user_tenant_roles`: `branch_id` (FK→branches), `department` VARCHAR(120). NULL=all.
- `platform.set_membership_scope(actor, utr_id, branch, dept)` SECURITY DEFINER, search_path pinned: re-checks `is_super_admin OR user_has_permission(actor,'tenant.users.update', row_tenant)`; validates branch is active branch of the row's tenant; UPDATEs **ONLY branch_id/department — never role_id** (no escalation path). Errors: no_data_found/insufficient_privilege→403, FK/integrity→409.

**DISPLAY-ONLY proven**: whole-`database/` grep confirms branch_id/department appear ONLY inside the branch section — NOT in resolve_user_permissions / user_has_permission / get_user_menus / v_user_permissions (all redefined/hardened in 11). Backend regression test `Owner_SetScope_DoesNotChangeEffectivePermissions` is genuine (calls real resolve_user_permissions before/after, asserts equal + non-empty).

**Backend** (all untracked new files + AdminController diff): BranchDirectory (read, RLS + explicit tenant predicate), BranchRepository (direct EF insert, RLS WITH CHECK bounds to JWT tenant), MembershipScopeWriter (routes through set_membership_scope). Endpoints: GET branches [tenant.users.read], POST branches [tenant.settings.update], PUT users/{id}/scope [tenant.users.update]. People-list (UserDirectory) surfaces branchId/branchName/department on UserListItemDto.
- NOTE (INFO, not a finding): UserDirectory branchName lookup `db.Branches.Where(branchIds.Contains(...))` has no explicit tenant predicate — relies solely on RLS (branchIds derive from this-tenant memberships; RLS covers it). Defense-in-depth only; not a leak.

**Tenant isolation**: BranchScopeWebAppFactory seeds a genuine 2nd tenant (OtherTenantId) with OtherBranchId; `Owner_ListBranches_IsTenantIsolated` asserts it never leaks. App runs as docslot_app (real RLS path).
