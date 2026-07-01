---
name: branch-scope
description: Branch/department SCOPE (#90) in the Team console — People SCOPE column + All-branches filter + N-branches header stat + manage-panel scope control. Endpoints, seam fns, display-only rule.
metadata:
  type: project
---

# Team console — Branch/Department SCOPE (#90, epic #80, frontend, 2026-07-01)

SCOPE is a **DISPLAY-only** org attribute (branch + department) on a tenant membership. It **never confers permissions** — the server write goes through `platform.set_membership_scope`, which touches ONLY `branch_id`/`department`, never `role_id`. So the scope control is editable even for SELF (unlike role/lifecycle actions, which are self-lockout hazards and stay hidden for self).

## Endpoints consumed (`AdminController`, base `/api/v1`)
- `GET /tenants/{tenantId}/branches` (gated `tenant.users.read`) → bare `BranchDto[]`.
- `PUT /tenants/{tenantId}/users/{userId}/scope` (gated `tenant.users.update`, Idempotency-Key) → `SetMemberScopeResult {userTenantRoleId, branchId, department}`.
- `POST /tenants/{tenantId}/branches` (gated `tenant.settings.update`) EXISTS but is **NOT wired** — no branch create/management UI was in scope. GAP: a fresh tenant with 0 branches shows only "All branches" + the `noBranches` hint in the picker until branches are created out-of-band. A branches-admin screen is the follow-up.
- Additive nullable fields on `UserListItemDto`: `BranchId`, `BranchName` (server-resolved), `Department`. Null branch → "All branches"; null/blank department → "All departments".

## Where it lives
- Contracts (`lib/mock/contracts.ts`): `BranchSchema`/`Branch`; `SetMemberScopeResultSchema`/`SetMemberScopeResult`; `SetMemberScopeRequest` (plain TS interface, request input). Scope fields added to BOTH `UserListItemSchema` (`.default(null)`) + `UserListItemDtoSchema` (`.nullable().optional()`, passthrough).
- Seam (`lib/backend/{real,index}.ts`): `listBranches` (real pass-through parse; mock `rbac.listBranches` = 3 seeded branches), `setMemberScope(userId,{branchId,department},idem)`. Mock `setMemberScope` MUTATES the USERS seed so flag-off persists + optimistic reconciles — the ONLY user mutation in `rbac.ts` that isn't a no-op.
- Hooks (`features/team/api.ts`): `useBranches(enabled=true)` (key `['team','branches']`, 5min staleTime; pass `enabled=canReadUsers`). `useSetMemberScope()` = optimistic `onMutate` cache patch of `['team','users']` (row branch/branchName/department flip instantly, `branchName` threaded in vars for the label) → `onError` rollback → `onSettled` invalidate.
- UI: `UsersTab.tsx` (SCOPE column `hidden lg:block w-32` = branch + dept sub-line; 2nd toolbar dropdown All-branches, client-side `u.branchId===filter`, disabled when 0 branches, reset in `clearFilters`). `TeamScreen.tsx` (`{{count}} branches` header stat). `ManageUserPanel.tsx` (`ScopeSection`, keyed by userId, rendered when `can('tenant.users.update')`, self-editable; branch Select + department TextInput + Save-when-dirty; branches read has skeleton/error+retry/empty states).

## Perms + i18n
- Perms: `tenant.users.read` (branches + People), `tenant.users.update` (scope write + ScopeSection). No new nav keys; no `role===`.
- i18n: `team.{statBranches,allBranches,allDepartments}`, `team.manage.{scopeHeading,scopeSub,branchLabel,branchAll,branchesError,noBranches,departmentLabel,departmentPlaceholder,departmentHint,saveScope,scopeSaved}` (en+hi, parity enforced).
- No new routes / URL panel params (scope lives inside the existing `manageUser` slide-over). typecheck + build green.

See also [[iam-matrix]] (the Team console it extends) and the frontend consumption note in `.agents/memory/api-contracts.md`.
