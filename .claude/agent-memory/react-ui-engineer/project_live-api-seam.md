---
name: live-api-seam
description: How the frontend talks to the LIVE .NET API behind VITE_USE_REAL_API — seam layout, what is wired (reads + writes), enum/shape quirks, and open gaps.
metadata:
  type: project
---

# Live-API seam (VITE_USE_REAL_API)

The frontend can talk to the LIVE .NET API (http://localhost:5054, Vite-proxied `/api`, api-client BASE `/api/v1`) behind `import.meta.env.VITE_USE_REAL_API` (truthy → real; unset/falsy → mock, byte-for-byte unchanged). Toggle: `VITE_USE_REAL_API=1 npm run dev`.

**Why:** ship a real-backed admin app without disturbing the mock prototype; mock stays the default so design/QA work continues offline.

**How to apply:** seam lives in `frontend/src/lib/backend/` — `flag.ts` (`USE_REAL_API`), `real.ts` (apiFetch → zod-parse RAW DTO → ADAPT to the existing app-facing shape), `index.ts` (per-fn real-vs-mock facade), plus `patients-mock.ts` and `mutations-mock.ts` (mock fallbacks for fns that have a live endpoint but no pre-existing mock). Feature `api.ts` files import WIRED fns from `@/lib/backend`; anything not wired keeps importing `@/lib/mock` directly. NEVER change a feature screen to branch on the flag — branch in the seam. Errors: `toUserError(e)` pulls `message.responseMessage` from the .NET error envelope.

## Wired LIVE
- Reads: auth (login/getMe/logout/refresh), getPermissions, getMenus (normalizes `/dashboard`→`/`), getBadges (real `/me/badges`), listBookings, listPatients, listDoctorCards, getDashboardSummary, getAnalytics(period).
- Writes (all POSTs send `Idempotency-Key` via apiFetch `idempotency:<stable key>`, key generated ONCE by the caller at action start, reused on retry): approveBooking / cancelBooking(body `{reason}`) / completeBooking / noShowBooking → `/bookings/{id}/{action}`; createBooking → `/bookings`; addPatient → `/patients`; **addDoctor → `/doctors`** (POST now exists). Every booking mutation `invalidateBookingViews()` = `['bookings','list']` + `['dashboard','summary']` + `['me','badges']`; addDoctor invalidates `['doctors','cards']`.
- **Booking detail**: `getBooking(id)` → `GET /bookings/{id}` (returns BookingListItemDto) adapted to the panel `Booking` shape; mock resolves `BOOKINGS.find`. The manage/approve/conversation panels now open by `bookingId` (NOT a full Booking payload) — `BookingPanelLoader.tsx` fetches via `useBookingDetail` and renders skeleton/error before the inner panel. Panel payload in `stores/ui.ts` is `{type, bookingId}`; SlideOverHost restores from URL by id alone (no BOOKINGS.find).
- **NewBooking wizard live**: `listPractitioners(deptId)` → `GET /doctors` filtered client-side by departmentName→mock-dept-id; `listSlots(doctorId, date?)` → `GET /doctors/{id}/slots?date=YYYY-MM-DD` keeping only `status==="available"`, adapting each to `Slot` carrying `slotId` (the GUID the create needs). Wizard holds the full `Slot` and sends `slot.slotId ?? slot.time`. Date defaults to istToday() (Asia/Kolkata). Seeded slots: ~5/doctor/day at 15:00–17:00 today + next 2 days.
- **Developers + Security READ lists wired (PASS 4, 2026-06-16)** — PLATFORM-ADMIN consoles. Reads: `listApiClients` (derives `status` from isActive/isVerified — DTO omits it), `listScopes`, `listEventTypes`, `listWebhooks` (fans out per-client: GET /api-clients then GET /webhooks?clientId= each, flatten — NO list-all endpoint), `verifyAuditChain`, `listAnchors`, `listDpdpRequests`, `listBreaches`, `listReviewQueue`, `listKeyStatus`. All bare arrays, contracts 1:1 (pure pass-throughs except the two reconciliations noted). DANGEROUS PLATFORM WRITES deliberately NOT wired (break-glass/breach-report/DPDP-export+erase/deletion-cert/anchor/client-register-rotate-status-scopes-ratelimits/createWebhook) — stay mock/best-effort. `deletion-certificates` endpoint exists but is NOT consumed by any security hook (SecurityScreen has no cert-list tab) so it was left unwired.
  - **CRITICAL: verify Developers/Security with `admin@docslot.io / admin` (platform-admin), NOT priyanka.** Only the platform-admin's live `/me/menus` returns `developers`+`security` (12 top nodes); priyanka (tenant_owner) gets neither + 403 on the endpoints. The MOCK seam separately grants priyanka these menus (its own fixture) — so in MOCK mode priyanka DOES see them; only LIVE mode hides them for her. See [[platform-admin-vs-tenant-login]].
- Still mock regardless of flag: agent/department/floor dashboard widgets, calendar, clinical, commission, team, sendPaymentLink, conversation thread (`useConversation`), developer/security MUTATIONS + request-logs + webhook-deliveries (no live endpoint).

## Token refresh (api-client.ts)
Access token = short-lived **15-min** JWT (`expiresInSeconds:900`); refresh tokens **ROTATE** (old one 401s after one use). `apiFetch` does **401 → refresh → retry** transparently: on a 401 (except `/auth/login|refresh|logout`) it calls `refreshAccessToken()` (a **single-flight** shared promise — mandatory, since a burst of parallel refreshes would rotate the token out from under each other and kill the session), updates the session store, and replays the request once with the new Bearer. Refresh failure → `clear()` (route guard bounces to /login on next nav). Before this (pre-2026-06-22) nothing called `refresh()`, so sessions died after 15 min with an app-wide 401 storm. Verified by `frontend/qa/verify-refresh.mjs` (login → corrupt access token, keep refresh token → reload → asserts ONE /auth/refresh + all core reads recover to 200). `TokenResponse`: `{accessToken, refreshToken, expiresInSeconds, userId, activeTenantId, mfaRequired}`.

## Shape quirks (verified live)
- Booking enums (`/bookings` status/source/gender/language) are snake_case STRINGS now (were ints in pass 1 — int shim removed). `BookingRowSchema` is the enum gate.
- `/dashboard/summary` `noShowRate` is a FRACTION 0..1 (×100 for the % card). `/analytics` `noShowRatePct`/`whatsappSharePct` are already 0..100.
- Analytics funnel comes as free-text `stage` strings → mapped to fixed enum keys `startedChat…confirmed` BY ORDER (first 5). KPI cards have no period-over-period delta from the API → deltaPct=0 (arrows neutral).
- `/doctors` enriched: departmentName, todayBooked/Capacity, rating, todayHoursStart/End ("HH:mm:ss"), nextAvailableSlot (nullable ISO). `deptId` mapped from departmentName→mock dept id so filter tabs match. No qualification/room on the DTO; nextAvailableSlot null in seed → "— IST".

## Closed gaps (live-final pass, 2026-06-16)
All three pass-1 gaps are now wired and proven end-to-end live:
- `POST /doctors` exists → AddDoctorPanel saves live (CreateDoctorRequest: fullName req; departmentId?/specialization?/qualifications?(string[])/consultationFee?/phone?/gender?/experienceYears?/isAcceptingNewPatients=true). Panel's deptId is a MOCK token (not a department GUID) → sent as `departmentId:null` + department NAME as `specialization` (directory derives the tab from spec/name). `room` has no column → dropped. Gate moved to `docslot.doctor.create` (added to mock perm set too, additive/no UI change).
- createBooking from the wizard SUCCEEDS live (real doctorId + slotId).
- Manage/Approve slide-overs OPEN for real bookings via getBooking(id).

## Nav route coverage (live `/me/menus`)
The live backend serves 12 top-level nodes incl. `lab` (route `/lab`, shown to hospital/pathology_lab/diagnostic_center — Apollo is a hospital). **Sidebar renders ONLY top-level nodes** (`menus.map`, no recursion), so child nodes (`/bookings/today`, `/patients/clinical`, `/care-partners/payouts`, `/settings/users`, …) are never clickable and don't 404 — but every *top-level* route MUST exist in the SPA or it falls through to the **root** notFound (renders the bare 404 WITHOUT AppShell, since the authed layout never matched). `normalizeRoute` in `real.ts` maps server→SPA routes (`/dashboard`→`/`). `/lab` had no route → fixed by adding a `/lab` route → `<PlaceholderScreen titleKey="nav.lab" />` (same stopgap as `/settings`), bilingual `nav.lab`, and `flask`→FlaskConical / `gear`→Settings in `icons.tsx`. When adding a backend menu item, add the matching SPA route (or a placeholder) in the same change.

## Open gaps / quirks
- `GET /bookings/{id}` returns MASKED phone only (e.g. `+91xxxxxxxx72`) — PatientChip renders `booking.phone` as that masked value in live mode (raw never sent). The DTO carries no doctorId → adapter sets `doctorId:''`; fine because approve/cancel key off booking id, not doctorId.
- Conversation panel's thread is still mock-only (no live conversation endpoint), so opening it for a real booking shows the detail header but an empty/мock thread.

## Verification harnesses
- `frontend/qa/live-final2.mjs` (live, BASE :5174) — manage panel populates from GET /bookings/{id}, approve pending→confirmed (asserted via API), wizard create appears in list + badge increments, add-doctor increments Practitioners + card appears; harvests created booking/doctor ids for cleanup; asserts zero console errors. Shots → `qa/shots/live-final2/`.
- `frontend/qa/mock-final2.mjs` (mock, BASE :5173) — manage panel + wizard complete on the mock seam (flag off), zero errors.
- `frontend/qa/live-writes.mjs` (older live harness) still works for analytics/badge/add-patient.
- `frontend/qa/live-admin.mjs` (live, BASE :5173) — PASS 4: logs in as **admin@docslot.io/admin**, asserts nav shows Developers+Security, API clients=4 real, scopes render, webhooks empty-state, audit verify result, breaches=2, dpdp=2, keys=6, review-queue empty-state, then priyanka login → NO Developers/Security in nav. Zero errors. Shots → `qa/shots/live-admin/`. `frontend/qa/mock-admin-smoke.mjs` re-verifies mock-default unchanged (mock 4 clients + 2 mock webhooks + broken-chain + breaches). These are READ-ONLY (no DB cleanup needed — no writes fired).
- Cleanup after a live run (demo tenant `11111111-1111-1111-1111-111111111111`, DB `docslot_platform`): delete created booking + its `opd_tokens`/`slot_holds`, free its `time_slots` row (`current_count=0, status='available'`), hard-delete the created doctor, delete the wizard patient + its `patient_tenant_links`, then `psql -d docslot_platform -f database/reset_demo_state.sql` to restore the approved seeded booking to pending. Baseline = 8 doctors; bookings pending=4/confirmed=3/completed=2/no_show=1. Dev rate limit 5000/min.

See also [[contract-surface]] (mock adapter signatures the backend mirrors) and [[foundation-patterns]] (seam/permission/i18n layout).
