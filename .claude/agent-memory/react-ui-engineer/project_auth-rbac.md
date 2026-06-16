---
name: auth-rbac
description: Slice 01 frontend — auth/login + session store + route guard + Team & Roles RBAC admin, and the exact permission keys gating each action.
metadata:
  type: project
---

Slice 01 (platform_core) frontend shipped auth + the Team & Roles admin. Verify files still exist before relying on them.

## Auth & session
- **Session store** `stores/session.ts` (Zustand + `persist` → localStorage key `docslot.session`). Holds `accessToken/refreshToken/tenantId/user(MeDto)`. Selectors `isAuthenticated()`, `activeTenant()`. `getSessionSnapshot()` is a NON-React getter the api-client uses (can't call hooks) for the bearer token + `X-Tenant-Id`.
- **api-client** now pulls token + tenant from the session snapshot (falls back to `VITE_DEV_BEARER`/none). Explicit `req.tenantId` still wins.
- **Auth hooks** `features/auth/api.ts`: `useLogin` (sets session), `useMe` (bootstraps MeDto when a token exists, `enabled: isAuthed`, drives the shell loader), `useLogout` (clears session + `qc.clear()`).
- **Login** `features/auth/LoginScreen.tsx` — standalone full-screen (NOT inside AppShell), rhf+zod dependency-free resolver, bilingual, surfaces mock 401 `auth.error.invalid` / 423 `auth.error.locked`. Demo creds shown on screen: `priyanka@apollocare.in` / `reception` (`DEMO_LOGIN` from lib/mock).
- **Mock auth** in `lib/mock/rbac.ts`: `login/refresh/logout/getMe`, throws `MockApiError(status, messageKey)`; lockout after 5 bad attempts per email (uniform error → no enumeration).

## Router (REFACTORED to two layers — don't flatten it)
`app/router.tsx`: root = bare `<Outlet/>`; children = `/login` (standalone, `beforeLoad` bounces already-authed → `/`) + a **pathless `authed` layout** (`id:'authed'`) whose `beforeLoad` redirects unauthenticated → `/login?redirect=<path>` and whose component is `AppShell`. All app routes (incl. `/team`) are children of the `authed` layout. Slide-over `panel`/`id` search params stay validated on the root. Logout navigates `{ to:'/login', search:{ redirect: undefined } }` (the redirect param is required by the type).

## Team & Roles (`/team`, replaces placeholder)
- `features/team/TeamScreen.tsx` — Radix Tabs (Users | Roles). `api.ts` has all hooks. Panels in `features/team/components/`: InviteUserPanel, ManageUserPanel, RoleViewPanel, CreateRolePanel. New Panel union types in `stores/ui.ts`: `inviteUser`, `manageUser{userId}`, `roleView{roleId}`, `createRole` — wired in SlideOverHost (manageUser/roleView are URL-restorable via `?panel=&id=`).
- **ManageUserPanel** has 3 sections: roles (assign/revoke), permission overrides (inline grant/deny form — **reason MANDATORY**, deny-wins stated, expiry none/30/90d, dangerous-permission warning), and the **effective-permission explainer** (mirrors `v_user_effective_permissions`: each key tagged source `role`(+via name) or `override_grant`).
- **Permission keys gating actions** (all real, verified in SQL seed): Invite user → `tenant.users.create`; assign/revoke role → `tenant.roles.assign`; overrides → `platform.overrides.grant`; create role → `platform.roles.manage`; rows interactive when `tenant.roles.assign || platform.overrides.grant`.
- **Demo user is a Tenant Admin** — `getPermissions()` seed (`SIGNED_IN_PERMISSIONS` in `lib/mock/index.ts`) now includes the team-admin keys so the surface is exercisable.

## Mock contracts added (lib/mock/contracts.ts) — mirror mediq.SharedDataModel/Docslot/{Auth,Admin}
Auth: `LoginRequest, TokenResponse, MeTenant, Me`. Admin: `UserListItem(+maskedPhone, roles[]), CreateUserRequest/Result, Role, PermissionDef(+isDangerous), AssignRoleRequest/Result, SetOverrideRequest/Result(reason mandatory), UserOverride, EffectivePermission(source: role|override_grant)`. Functions in `lib/mock/rbac.ts`, re-exported via `lib/mock/index.ts` (`export * from './rbac'`). All mutations take a stable `idempotencyKey` (caller-generated once per action; de-duped via idemCache).

## Contract gaps flagged to orchestrator
- **No role-create endpoint** in Slice 01 backend (report listed users/roles read + role-assignment + override POSTs only). Frontend gates Create-role on `platform.roles.manage` and calls a mock `createRole` — backend needs a `POST /roles` (or equivalent) + the permission. 
- **Revoke role** is a mock toast only (no backend revoke endpoint surfaced yet).
- `tenant.users.read` is the backend gate for the users list (per Slice 01 report) — the list itself isn't separately gated in UI (the whole /team route is reachable from the menu, which is permission-filtered server-side).

## Build
typecheck + build green. Single chunk now ~769kB (code-splitting still deferred). New i18n namespaces: `auth.*`, `team.*` (en+hi, compiler-enforced parity). New format helpers `shortDate`/`dateTime` (IST) in `lib/format.ts`.
