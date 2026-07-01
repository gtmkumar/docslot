---
name: iam-matrix
description: Team & Roles "Roles & permissions" IAM surface — privilege matrix, role CRUD, and the catalog plane (+ Module / + Permission). Gating split, seam fns, i18n keys.
metadata:
  type: project
---

# IAM — Roles & permissions (feature `team`, Slice 2)

The IAM surface lives in `frontend/src/features/team/` (NOT a separate `iam` folder). As of epic #80 Phase A (2026-07-01) `TeamScreen.tsx` is a **six-tab unified console** — see the "Unified console" section below (it superseded the old two-tab Users/Roles layout). The **privilege matrix** body is now `RoleMatrixView.tsx` (the heart of the screen — grouped module sections, optimistic cell toggle via `useOptimistic`, dangerous-cell inline confirm, and — added in #83 — a legend, a scoped-effect tile from `matrix.scope`, and the full `permissionKey` per cell; plus the duplicate CTA for read-only/built-in roles). `RoleMatrixPanel.tsx` is now a thin SlideOver wrapper around `RoleMatrixView` (kept only for the `?panel=roleMatrix&id=` deep link). **`RolesTab.tsx` was REMOVED** — the role list + catalog toolbar now live inside `RolesPermissionsTab.tsx`.

## Two governance planes (DO NOT conflate the gates)
- **Assignment plane** (tenant-governed): grant/revoke an *existing* permission to a role; duplicate a role. Gated `tenant.roles.assign`. A tenant owner (priyanka) holds this.
- **Catalog plane** (platform-governed): define a *new* module or permission. Gated **`platform.permissions.manage`** (super_admin holds it; tenant owner does NOT). The "+ Module" / "+ Permission" buttons render ONLY for a holder — invisible to priyanka. Catalog toolbar sits above the role list in `RolesTab.tsx`.
- Create-role is a third, separate gate: `platform.roles.manage` (button in `TeamScreen.tsx` roles tab).

## Catalog-create panels (added 2026-06-26)
- `CreateModulePanel.tsx` (`?panel=createModule`) — fields resourceKey [lower_snake], name, description?, displayOrder?. → `POST /iam/modules` body `{resourceKey,name,description?,displayOrder?}` → 201 `{resourceTypeId}`.
- `CreatePermissionPanel.tsx` (`?panel=createPermission`) — module picker (resource = chosen module's resourceKey, from `useModules`), action [lower_snake], auto-previewed-but-editable permissionKey (`<resource>.<action>`, stops auto-syncing once hand-edited via a `keyTouched` flag), scope select platform|tenant|self, description, isDangerous toggle. → `POST /iam/permissions` body `{permissionKey,resource,action,scope,description,isDangerous?}` → 201 `{permissionId}`. Both 403/409 → `toUserError` toast.
- **Inert caveat** is a real correctness note shown bilingually (`team.catalog.permission.inertNote`): a new permission is grantable in the matrix but enforces NOTHING until application code checks it.
- Both panels: react-hook-form + zod resolver (the established panel pattern — NOT useActionState), Idempotency-Key via `idempotencyKey()` once per submit, registered in `stores/ui.ts` (payloadless, URL-addressable), `app/router.tsx` panel enum, `SlideOverHost.tsx` (lazy import + PAYLOADLESS + render case).
- On success both invalidate (via `invalidateCatalog` in api.ts): `modulesQueryKey`, `permissionRegistryQueryKey`, `['team','iamPermissions']`, `['team','roleMatrix']` (prefix — matrix is per-role keyed) so a new permission shows up as a matrix cell.

## Seam fns (live + mock, flag-blind)
- `createModule(req, idemKey)` / `createPermission(req, idemKey)` in `lib/backend/{real,index}.ts`. Hooks `useCreateModule`/`useCreatePermission` in `features/team/api.ts`.
- Contracts in `lib/mock/contracts.ts`: `CreateModule{Request,Result}Schema`, `CreatePermission{Request,Result}Schema`. permissionKey regex = dotted lower_snake `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$`.
- Mock impls in `lib/mock/rbac.ts` MUTATE the in-memory catalog so flag-off reflects the create: `MODULE_META` + `EXTRA_MODULES` (replaced the frozen `RESOURCES_IN_ORDER` IIFE with a `resourcesInOrder()` fn so new modules appear in `listModules`), and `PERMISSION_REGISTRY.push` so the new permission renders as a matrix cell. Exposed via the `@/lib/mock` barrel (`export * from './rbac'`).

## Team & Roles last-5 fns now LIVE (Slice 13, 2026-06-30)
The final mock-only `team` IAM fns are wired behind `VITE_USE_REAL_API` in `lib/backend/{real,index}.ts`, re-pointed in `features/team/api.ts` (only the `CreateRoleRequest` TYPE still imports from `@/lib/mock`):
- `createRole(req,idem)` → `POST /api/v1/roles` (perm `platform.roles.manage`) body `{roleKey,name,description:null,tenantId:null,scope:'tenant'}` → `{roleId}` (CreateRoleResultSchema, NEW in contracts.ts). **The create DTO has NO permissionKeys — the role is created EMPTY.** Initial picks are attached AFTER create via the per-grant guarded `grantRolePermission(roleId,permissionId,…)` loop (each runs the DB no-escalation guard; NEVER bulk-grant). The picker collects permissionKeys → resolve to permissionId via the wired `listIamPermissions()` catalog. Per-grant idem key = `${idem}:grant:${permissionId}` so retries dedupe without collapsing grants. Returned Role built client-side (scope tenant, isSystem false, tenantId = session tenant).
- `getPermissionRegistry()` — DERIVED from wired `listIamPermissions()` (no own endpoint): map `PermissionDto`→`PermissionDef` (drop permissionId; null description → '').
- `getRolePermissions(roleId)` — DERIVED from wired `getRoleMatrix()`: flatten `modules[].cells[]`, keep `granted`, map to `permissionKey` (`string[]`).
- `listUserOverrides(userId)` → NEW `GET /api/v1/iam/users/{id}/overrides` (gated **`platform.overrides.read`** — SoD-distinct from `.grant`). `UserPermissionOverrideDto` matches `UserOverrideSchema` 1:1 (pass-through). The `OverridesSection` in `ManageUserPanel.tsx` is ALREADY gated on `platform.overrides.grant` (line ~79) and the mock perm seed has BOTH read+grant, so no new JSX gate was added (a grant-without-read SoD edge 403s → degrades to empty state, which the task blessed).
- `getEffectivePermissions(userId)` → NEW `GET /api/v1/iam/users/{id}/effective-permissions` (gated `tenant.users.read`). `EffectivePermissionDto.via` is ALWAYS null live (the view carries no role attribution); `EffectivePermissionSchema` allows it; `EffectiveSection` renders `via ? · via : ''` gracefully.

## i18n
Namespace `team.catalog.*` (en + hi both present, ~line 472 / ~1750 in `app/i18n.ts`): `toolbarLabel/addModule/addPermission/optional`, `module.*`, `permission.*` (incl. `scopeOption.{platform,tenant,self}`, `inertNote`, `keyHint`), `validation.*`.

## Unified Team & roles console (epic #80 Phase A — #81/#82/#83, 2026-07-01)

`TeamScreen.tsx` → six top-level Radix tabs, each gated on a real permission via in-memory `can()` (first *visible* tab is the default so we never open a hidden tab):
- **People** (`tenant.users.read`) → `UsersTab.tsx`. **Roles & permissions** (`tenant.users.read`) → `RolesPermissionsTab.tsx`. **Invites** (`tenant.users.read`) → empty-state. **Audit log** (`tenant.audit.read`) → empty-state. **Security** (`tenant.settings.read`) → empty-state. **API & integrations** (`platform.api_clients.manage`) → `ApiIntegrationsTab.tsx`.
- Invites/Audit/Security are **design-token empty-states** (icon + en/hi copy), NOT fake data — real backends land in #89 (invitations) / #86 (audit read-API) / #91 (security policies).
- Header stat line: `{{count}} active` (loaded users list) + `{{count}} roles ({{custom}} custom)` (`useRoles`, custom = `!isSystem`) + `{{count}} branches` (#90, `useBranches(canReadUsers)`). (Pending-invites #89 badge lives on the Invites tab, not the header.)
- Toolbar (top-right): **Invite people** → `inviteUser` panel, gated `tenant.users.create`. **Export** + **Bulk import** are D4 (#95) — rendered `disabled` with a `title` "Coming soon" (visual parity, deliberately non-functional; never fake an export). Create-role stays a per-tab action (`platform.roles.manage`) using the `Tabs.Content forceMount asChild` header pattern.

**API & integrations tab (`ApiIntegrationsTab.tsx`)** COMPOSES the existing developer-portal sub-tab bodies (`ClientsTab`/`ScopesTab`/`WebhooksTab`/`LogsTab`) from `@/features/developers/components/*` inside a nested Radix Tabs — a deliberate **cross-feature import** (the epic folds the dev portal into the console; reuse, don't duplicate). `/developers` and `/security` screens + routes are UNCHANGED. Management actions still gate on `platform.api_clients.manage`.

**People reskin (#82, `UsersTab.tsx`)** — UI-only over the live `UserListItemDto` (no backend change): YOU badge on the current user (`useSession((s)=>s.user?.userId)`); colour-coded role badges via a deterministic hash of `roleKey` → token-only `ROLE_COLORS` (primary/info/warn/accent/danger/neutral soft pairs — zero hex); an All-roles client-side filter (`Select` over distinct `roles[].roleKey`, wrapped in a fixed `w-40` div because Field's Select is `w-full`); a "showing X of Y" count; explicit 2FA On/Off pill; and a per-row `…` **kebab** replacing the whole-row button. Kebab items: Manage access → `manageUser` (holds deactivate/reset/assign-role + the #90 scope control), Edit profile → `editUser`, View effective access → `effectiveAccess` (viewers get only the last). SCOPE column + All-branches filter SHIPPED in #90 — see [[branch-scope]].

**Roles & permissions master-detail (#83, `RolesPermissionsTab.tsx`)** — nested Radix Tabs, 3 sub-tabs:
- **Roles & privileges**: role list (left, `aria-current` selection, default-selects `roles[0]` WITHOUT an effect via `selectedId ?? roles[0].roleId`) + always-on `RoleMatrixView` pane (right); catalog toolbar (+Module/+Permission, `platform.permissions.manage`) rides above.
- **Per-user overrides**: empty-state — tenant-wide overrides list awaits **#85**; per-user editing stays reachable via People → Manage access → overrides in `ManageUserPanel`.
- **Effective access**: person picker (users list) opening the reused `effectiveAccess` slide-over. Per-role member counts (#84) omitted.

**New reusable primitive:** `components/ui/KebabMenu.tsx` — accessible overflow menu (trigger `aria-haspopup=menu` + role=menu/menuitem, focus-first-item on open, Arrow up/down cycle, Esc returns focus, outside-click/Tab close). Built WITHOUT a Radix dropdown-menu dep — the repo ships only `@radix-ui/react-{dialog,hover-card,tabs}`; for menus/popovers follow this or the `AppearanceMenu` pattern, do NOT add a new Radix package.

No new routes, no new endpoints, no contract gaps — all Phase-A gaps are backend-not-yet-built (#84/#85/#86/#89/#90/#91/#95), surfaced as honest empty-states/stubs. `npm run typecheck && npm run build` green after each of the 3 commits (520c9d4 #81, then #82/#83 replayed to ad0bd24 / 3844fc6 to fold a select-width fix).

See also [[live-api-seam]] (the IAM READ/WRITE wiring + base path /iam) and [[platform-admin-login]] (super_admin vs tenant_owner perm/nav differences).
