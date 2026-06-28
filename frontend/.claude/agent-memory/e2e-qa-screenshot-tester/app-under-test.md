---
name: app-under-test
description: How to drive the DocSlot admin SPA in Playwright — URLs, login, auth model, panel deep-links
metadata:
  type: reference
---

DocSlot admin SPA (React 19 + Vite). Live E2E driven with Playwright (installed: `@playwright/test` 1.61, chromium present in ~/Library/Caches/ms-playwright). Existing QA scripts live in `frontend/qa/*.mjs`; screenshots go to `frontend/.qa-screenshots/`.

- SPA: http://localhost:5173 (live mode). API: http://localhost:5054, proxied via Vite at `/api` → `/api/v1`. `GET /health` returns "Healthy".
- Login: `page.goto('/login')`, fill `#login-email` + `#login-password`, click `button[type=submit]`, wait for URL to leave `/login`.
  - Super-admin: `admin@docslot.io` / `admin` (all perms incl `tenant.roles.assign`, `platform.roles.manage`). Tenant id seeded: `11111111-1111-1111-1111-111111111111`.
  - Tenant owner (no super powers): `priyanka@apollocare.in` / `reception`. Has `tenant.roles.assign` (sees Duplicate) but NOT `platform.roles.manage` (no "Create role").

**Auth is a bearer TOKEN held in JS, not a cookie.** Raw `page.evaluate(fetch('/api/...'))` returns 401 — it lacks the Authorization header. To inspect real API bodies, attach a Playwright `page.on('response')` listener and read bodies from the app's own XHRs (those carry the token and return 200). Do NOT trust raw in-page fetch for auth-gated endpoints.

**Slide-over panels are URL-addressable** via `?panel=<type>&id=<uuid>` (owned by `SlideOverHost`). Types: `roleMatrix`, `duplicateRole`, `manageUser`, `effectiveAccess`, `inviteUser`, `createRole` (id-less). You can deep-link directly, e.g. `/team?panel=roleMatrix&id=<roleId>`. Panels are right-side Radix Dialogs (`[role=dialog]`), ~420px wide, anchored right (x≈1020 at 1440 vw), Esc-closeable, focus-trapped — the correct CRUD modality (not centered modals).
