---
name: user-management-revamp
description: Team & roles Users-side revamp — editUser slide-over, user-lifecycle actions, list toolbar (search + status filter), permission keys consumed
metadata:
  type: project
---

The Users side of `/team` (TeamScreen → UsersTab + ManageUserPanel) has a full lifecycle UI.

**Permission keys consumed by the Users surface** (gate via `can()`, never role names):
- `tenant.users.create` → Invite button (in TeamScreen).
- `tenant.users.read` → row interactivity for read-only viewers (opens effectiveAccess).
- `tenant.roles.assign` / `platform.overrides.grant` → managers open manageUser panel.
- `tenant.users.update` → in ManageUserPanel, `canManage`: Edit profile (allowed for self), Deactivate/Reactivate + Reset access/Unlock (hidden for self via `isSelf`).

**editUser panel** is registered the same 4 spots as every other panel: `stores/ui.ts` union (`{ type:'editUser'; userId }`, URL-restorable), `app/router.tsx` panelSearchSchema enum, `components/layout/SlideOverHost.tsx` (lazy import + panelToSearch + searchToPanel + render case). Grep `editUser` to find all four.

**User-lifecycle hooks** (already in `features/team/api.ts`, do not modify): `useSetUserStatus` (deactivate/reactivate, reason mandatory), `useUpdateUser` (profile: fullName/phone/preferredLanguage; whitelisted, never email/status), `useResetUserAccess` (force password change + clear lockout). All take a caller-made `idempotencyKey()`; the DB re-checks self/last-admin guards and surfaces 403/409 via `toUserError`.

**UserListItem quirks**: `isActive` = active IN THIS TENANT (deactivated user still appears with isActive=false, roles=[]). `maskedPhone` is masked — EditUserPanel opens the phone input BLANK with a "leave blank to keep" hint and sends `null` when blank (never re-present the masked value as editable). `lockedUntil`/`mustChangePassword` drive the Locked / Reset-pending chips. "Locked" = lockedUntil in the future.

**Patterns established this slice** (reusable):
- List toolbar: debounced search (local `useDebounced` hook, 200ms) + 3-segment `role="radiogroup"` status filter mirroring ManageUserPanel's `ExpiryToggle` (token-driven `border-primary bg-primary text-bg` active). React Compiler memoizes the derived filtered list — no manual useMemo.
- Distinct empty states: tenant-has-no-users (`team.emptyUsers*`) vs filtered-empty (`team.emptyFiltered*` + Clear-filters action). Dim inactive rows with Tailwind `opacity-60` (the established dimming utility, not a hex token).
- Inline confirm + mandatory reason (override-form pattern): single `flow` state ('status'|'reset'|null), reason TextArea with `aria-invalid`, one stable `idempotencyKey()` per confirm, toast.success/toast.error(toUserError).
- InviteUserPanel: initialRoleId is REQUIRED (role-less user has no membership → invisible in list); options filtered to `scope==='tenant'`; toast distinguishes `result.alreadyExisted` (linked) vs new (invited).

**i18n**: new keys live under `team.{searchPlaceholder,filter*,emptyFiltered*,clearFilters,lockedChip,resetPendingChip}`, `team.account.*`, `team.edit.*`, `team.toast.*`, `team.invite.{selectRole,firstLoginNote}`. `team.validation` block (email/name/reason/permission/role/key) is shared by invite/edit/duplicate/createRole resolvers via `t(\`team.validation.${message}\`)`. No top-level `common.save` exists — use a feature-scoped save key (e.g. `team.edit.save`).
