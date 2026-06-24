---
name: project-impersonation
description: Support-impersonation (issue #3) frontend — JWT claim, begin/end endpoints, session slice, JWT decoder util, global banner
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
- `features/impersonation/api.ts` — `beginImpersonation()` / `endImpersonation()` helpers (parse + push token bundle through `setSession`) and `useEndImpersonation()` mutation (calls `qc.clear()` on success so the impersonated tenant's cached menus/permissions/lists don't bleed into the restored session). The begin UI/admin panel is NOT built yet — helpers exist for a future panel.
- `features/impersonation/components/ImpersonationBanner.tsx` — renders only when the claim is present; sticky `top-0 z-30`, `bg-warn-soft`/`text-warn` (warn palette = "acting outside normal scope"), prefers `user.tenants[].displayName` else a shortened UUID. Mounted in `AppShell` between `<Topbar/>` and `<main>`.
- i18n namespace `impersonation.*` (en + hi) added to `app/i18n.ts`.
- Permission keys consumed: none (claim-driven, no `can()` gate). No contract gaps — endpoint shapes matched the spec.
