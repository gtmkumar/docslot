---
name: iam-matrix
description: Team & Roles Slice 2 — the IAM privilege-matrix surface (live /api/v1/iam), its seam fns, panels, and gating
metadata:
  type: project
---

The Team & Roles "Roles" tab is now a full privilege-matrix admin UI wired to the LIVE .NET IAM API (`/api/v1/iam`) through the [[live-api-seam]]. Builds on the existing mock-backed team feature ([[screen-conventions]]).

**How to apply:** when extending Team & Roles or touching the matrix, reuse these — don't re-derive.

- **Panels** (`frontend/src/features/team/components/`): `RoleMatrixPanel` (the grid; opened by RolesTab rows via `openPanel({type:'roleMatrix',roleId})`), `DuplicateRolePanel` (clone built-in roles → navigates to new role's matrix), `EffectiveAccessPanel` (resolved key set grouped by resource; opened from UsersTab read-only rows + ManageUserPanel's "View full access" link). Old `RoleViewPanel` (`roleView`) still registered but no longer the default open.
- **Seam fns** (live/mock by `VITE_USE_REAL_API`, declared in `lib/backend/index.ts`): `listModules, listIamPermissions, getRoleMatrix, grantRolePermission, revokeRolePermission, duplicateRole, getEffectiveAccess` (new IAM) + `listRoles, listTenantUsers, setOverride` (existing endpoints moved into the seam). Real impls at the bottom of `lib/backend/real.ts`; mock impls at the bottom of `lib/mock/rbac.ts` (derive the matrix from `PERMISSION_REGISTRY`/`ROLE_GRANTS` + a mutable `MOCK_ROLE_GRANTS` overlay; synthetic `permissionId = perm-<key>`).
- **Matrix DTO carries everything the grid needs** — modules with per-cell `granted`/`isDangerous`/`moduleLicensed` and role-level `editable`/`isSystem`. The panel reads licensing/editability off the matrix itself; `useModules`/`useIamPermissions` hooks exist but aren't needed by the grid.
- **Cell toggles use `useOptimistic` + `useTransition`** (React 19 idiom): base map rebuilt from the matrix each render, flip applied in the transition; `onSettled` invalidate reconciles; a thrown 403 auto-reverts and toasts `toUserError(e)`. Dangerous cells = red-dot (`bg-danger`) + INLINE confirm row (warn palette) — NOT a centered modal (no alert-dialog dep in the locked stack).
- **Gating:** matrix/overrides/effective-access → `tenant.users.read`; toggle/Duplicate → `tenant.roles.assign`; scratch role → `platform.roles.manage`. DB re-checks built-in-role edits, so the UI gate only hides affordances.
- zod schemas: `lib/mock/contracts.ts` IAM section (`ModuleDtoSchema, IamPermissionDtoSchema, RoleMatrixSchema (+ RoleMatrixModule/Cell), RolePermissionToggleResultSchema, DuplicateRoleRequest/ResultSchema, EffectiveAccessSchema`). i18n: `team.matrix.*`, `team.duplicate.*`, `team.effective.*` (en+hi).
