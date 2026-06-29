---
name: frontend-form16a-pan
description: How the React SPA handles the full-PAN Form 16A TDS document without leaking it; the verified-absent leak vectors. Audited PASS.
metadata:
  type: project
---

The Form 16A (TDS 194H) certificate surfaces a FULL broker PAN, but ONLY inside the rendered HTML document, never in the SPA's data layer. Audited PASS on branch `feat/phase2-broker-portal-frontend` (Phase-2 broker-portal frontend slice).

**Pattern (the correct one — treat as the reference for any future full-PII document):**
- `openForm16ADocument(payoutId)` in `src/lib/backend/real.ts` (~line 1173): raw `fetch` (NOT `apiFetch`) with manual Authorization + X-Tenant-Id headers from `getSessionSnapshot()`, `res.blob()`, `URL.createObjectURL`, `window.open(url, '_blank', 'noopener,noreferrer')`, `setTimeout(revoke, 60_000)`.
- Deliberately bypasses `apiFetch` (src/lib/api-client.ts:118) because apiFetch is JSON-only (`JSON.parse`) and feature `api.ts` hooks wrap responses in TanStack Query cache. The document never enters that path → never cached.
- The cert DTO (`Form16ACertificateSchema`, src/lib/mock/contracts.ts:1341) carries `deducteePanLast4` only — NO full PAN field. PayoutsTab `setCert` (PayoutsTab.tsx:67) holds only this last-4 DTO. UI renders `panLast4: 'PAN ••••{{last4}}'`.
- The DTO also has a `documentUrl` string field, but it is INERT — no component reads it; the View action calls `openForm16ADocument(payout.payoutId)` which rebuilds the path. (Watch: never `window.open(cert.documentUrl)` directly — that would open WITHOUT auth and is the trap to reject.)

**Leak vectors verified ABSENT in the changed files:**
- No `console.*`, no analytics/telemetry/Sentry/gtag/posthog calls.
- No `localStorage`/`sessionStorage` writes of cert/html.
- Full-PAN html is a local `const` consumed straight into createObjectURL; never re-referenced, never set into React state / Zustand / Query cache.
- `useIssueForm16A` mutation `onSuccess` only invalidates the payouts query key — mutation results are not auto-cached by TanStack.

**Why:** backend slice already PASSed (see dashboard memory backend-phase2-form16a-tds — prior near-miss was full-PAN landing in the idempotency-cache, closed server-side via IDoNotCacheResponse). The frontend job was narrow: confirm the SPA does not re-introduce a leak. It does not.

**Residual / non-blocking observations (LOW/INFO, not vetoes):**
- 60s blob revoke is a fixed timer, not load-coupled. Acceptable: blob URLs are origin- + document-scoped, not guessable, and the new tab loads in ms. A tighter pattern (revoke on the new window's `load`) is possible but not required.
- Error path: `openForm16ADocument` throws on `!res.ok` BEFORE createObjectURL, so no URL is created to leak on the failure path. On the success path, if `window.open` is blocked by a popup blocker the timer still revokes — no retained reference.
- Session access/refresh tokens persist to localStorage via Zustand `persist` (src/stores/session.ts) — PRE-EXISTING from earlier waves, out of scope for this slice, not a regression. The PAN document never touches this store.

**How to apply:** when reviewing any future full-PII/PHI document surface in the SPA, require this exact transient-blob shape and re-run the leak-vector grep (console/storage/telemetry/state). Reject any direct `window.open(dto.url)` on an auth-required document.
