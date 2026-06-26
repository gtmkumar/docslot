---
name: iam-matrix
description: Team & Roles "Roles & permissions" IAM surface — privilege matrix, role CRUD, and the catalog plane (+ Module / + Permission). Gating split, seam fns, i18n keys.
metadata:
  type: project
---

# IAM — Roles & permissions (feature `team`, Slice 2)

The IAM surface lives in `frontend/src/features/team/` (NOT a separate `iam` folder). `TeamScreen.tsx` has two Radix tabs (Users / Roles); `RolesTab.tsx` lists roles and opens the **privilege matrix** slide-over (`RoleMatrixPanel.tsx`, the heart of the screen — grouped module sections, optimistic cell toggle via `useOptimistic`, dangerous-cell inline confirm, duplicate CTA for read-only/built-in roles).

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

## i18n
Namespace `team.catalog.*` (en + hi both present, ~line 472 / ~1750 in `app/i18n.ts`): `toolbarLabel/addModule/addPermission/optional`, `module.*`, `permission.*` (incl. `scopeOption.{platform,tenant,self}`, `inertNote`, `keyHint`), `validation.*`.

See also [[live-api-seam]] (the IAM READ/WRITE wiring + base path /iam) and [[platform-admin-login]] (super_admin vs tenant_owner perm/nav differences).
