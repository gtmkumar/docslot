---
name: project-impersonation
description: Support-impersonation (issue #3) frontend — JWT claim, begin/end endpoints, tenants picker, session slice, JWT decoder, banner + begin slide-over + Topbar trigger
metadata:
  type: project
---

Issue #3 support impersonation, frontend banner + helpers (backend already built/merged).

**Why:** A support actor can act inside a customer tenant. The backend mints an access token with a server-signed `impersonated_tenant` JWT claim (target tenant UUID). The UI must make this state unmistakable and offer a one-click exit.

**How to apply:** When touching impersonation UI, the claim on the live access token is the SINGLE source of truth for "am I impersonating right now?" — don't add parallel boolean state.

Key facts:
- Endpoints: `POST /api/v1/auth/impersonation/begin` (body `{ targetTenantId, reason, refreshToken, targetUserId?, ttlMinutes?, breakGlass? }`) → `{ token: TokenResponse, impersonationId, targetTenantId, expiresAtUtc }`; `POST /api/v1/auth/impersonation/end` (body `{ impersonationId, refreshToken }`) → a CLEAN `TokenResponse` (no claim). Zod schemas live in `lib/mock/contracts.ts` (Begin/EndImpersonationRequest/Result).
- `lib/jwt.ts` — base64url JWT *payload* decoder (no signature verify, no new dep). `readImpersonatedTenant(token)` returns the claim or null. Reuse this for any client-side claim read.
- Session store (`stores/session.ts`) gained `impersonationId` + `impersonatedTenantId`, with `setImpersonation()` / `clearImpersonation()`; `clear()` (logout) resets them. The /end call NEEDS `impersonationId`, which only the /begin response provides — so the begin helper persists it.
- `features/impersonation/api.ts` — `beginImpersonation()` / `endImpersonation()` helpers (parse + push token bundle through `setSession`); mutations `useEndImpersonation()` + `useBeginImpersonation()` (both `qc.clear()` on success so cached menus/permissions/lists re-bootstrap under the new scope); `useTenants(enabled)` query (`GET /tenants`).
- `features/impersonation/components/ImpersonationBanner.tsx` — renders only when the claim is present; sticky `top-0 z-30`, `bg-warn-soft`/`text-warn` (warn palette = "acting outside normal scope"), prefers `user.tenants[].displayName` else a shortened UUID. Mounted in `AppShell` between `<Topbar/>` and `<main>`.
- **Begin UI (now built):** `features/impersonation/components/BeginImpersonationPanel.tsx` — house slide-over (URL-addressable `?panel=beginImpersonation`, payloadless). Target-tenant `<Select>` (loading/error/empty/ready states), required reason, optional break-glass checkbox. Wired in `stores/ui.ts` Panel union + `SlideOverHost` (lazy + render case + PAYLOADLESS) + `router.tsx` `panelSearchSchema` enum (MUST add new payloadless panel names there or typecheck fails on the navigate search writer).
- **Trigger:** `components/layout/TopbarActions.tsx` — "Impersonate" ghost icon button gated on `can('platform.users.impersonate')` (in-memory, no role check). UI path: Topbar → Impersonate → pick tenant → reason → Start.
- **Tenants endpoint:** `GET /api/v1/tenants?skip&take` → `TenantDto[]` `{ tenantId, tenantCode, displayName, tenantType, primaryEmail, status, country, city? }`, gated `platform.tenants.read` (super_admin). Lives at `AdminController` `[Route("api/v1")]`. Zod `TenantListItemSchema` in contracts; `real.listTenants` (pass-through) + `listTenantsMock` (3 seed tenants) wired in `lib/backend/index.ts` as `listTenants`.
- i18n: `impersonation.*` (incl. `impersonation.begin.*`) + `topbar.impersonate` (en + hi) in `app/i18n.ts`.
- Permission keys consumed: `platform.users.impersonate` (trigger gate), `platform.tenants.read` (tenant list, server-enforced). No contract gaps — `/begin`, `/end`, `/tenants` shapes all matched.
