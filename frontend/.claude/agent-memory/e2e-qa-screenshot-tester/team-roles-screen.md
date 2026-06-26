---
name: team-roles-screen
description: Team & Roles screen — routes, selectors, and the roles/matrix/duplicate/override flows for QA
metadata:
  type: reference
---

Route `/team` (TeamScreen). Radix Tabs, `defaultValue="users"`. Tabs: `getByRole('tab',{name:/users|roles/i})`.

- **Roles list** (`RolesTab`): rows are `main ul > li > button`; each opens `?panel=roleMatrix&id=<roleId>`. Badge "System" (built-in) vs "Custom". Has skeleton/empty/error states. The list source is live `GET /api/v1/roles` (NOT `/iam/roles`, which 404s).
- **Matrix panel** (`RoleMatrixPanel`): grouped module `<section>`s, each `h3` + "granted/total" tally; action cells are `[role=checkbox]` with `aria-checked`. Built-in/non-editable → cells `disabled`, read-only notice, footer "Duplicate role" CTA (`Copy` icon). Dangerous cells carry a `.bg-danger` dot.
- **Duplicate** (`DuplicateRolePanel`): footer CTA → `?panel=duplicateRole`. Fields: `#dup-name`, `#dup-key` (lower_snake), `#dup-desc` (optional). Submit "Duplicate". On success navigates to the new custom role's matrix (cells become editable).
- **Custom matrix toggle**: normal cell click flips `aria-checked` optimistically (`useOptimistic`), POST grant / DELETE revoke under the hood. Dangerous cell click shows an INLINE warn-colored confirm step (Cancel / red Confirm) — no centered modal; it does not apply until Confirm.
- **Users tab** (`UsersTab`): rows `main ul > li button`. Super-admin (canManage) opens `manageUser`; read-only viewer opens `effectiveAccess`. `ManageUserPanel` hosts the effective-access viewer ("View all access") and the per-user overrides surface (override form requires a reason).

Matrix mutation endpoints are under `/api/v1/iam/roles/{id}/...` (matrix, permissions/{permId}, duplicate). The list/users endpoints are `/api/v1/roles` and `/api/v1/tenants/{id}/users`.
