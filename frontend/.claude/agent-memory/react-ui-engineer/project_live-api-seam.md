---
name: live-api-seam
description: The VITE_USE_REAL_API live-vs-mock seam (lib/backend) and verified .NET DTO quirks (int enums, dashboard field names)
metadata:
  type: project
---

The frontend can talk to the LIVE .NET API (http://localhost:5054, Vite-proxied `/api`) behind `VITE_USE_REAL_API`. Default/unset → mock seam, byte-for-byte unchanged. Toggle: `VITE_USE_REAL_API=1 npm run dev`.

**How to apply:** when wiring a new screen to the live API, add a real impl in `frontend/src/lib/backend/real.ts` (apiFetch → zod-parse RAW DTO → ADAPT to the existing app-facing shape), then export it from `frontend/src/lib/backend/index.ts` choosing real-vs-mock by `USE_REAL_API`. Point the feature `api.ts` import at `@/lib/backend` for ONLY the wired fns; everything else keeps importing `@/lib/mock`.

- Seam lives in `frontend/src/lib/backend/`: `flag.ts` (USE_REAL_API), `real.ts` (live impls + adapters + `toUserError`), `index.ts` (facade), `patients-mock.ts` (mock-side `listPatients` deriving PatientRow[] from lib/data PATIENTS). RAW DTO zod schemas are in `lib/mock/contracts.ts` under the "LIVE API — RAW LIST DTOs" section (BookingListItemDto, PatientListItemDto, DoctorDto, DashboardSummaryDto) plus app-facing `PatientRowSchema`.
- Wired live today (flag on): auth login/getMe/logout/refresh, getPermissions, getMenus, getBadges (stub → empty counts, no endpoint yet), listBookings, listPatients, listDoctorCards, getDashboardSummary. NOT wired (stay mock regardless): all mutations, practitioners/slots, agent panel, department-load, floor-doctors, analytics, calendar, clinical, commission, team, developers, security.
- **Success responses are RAW DTOs (no wrapper).** Errors are wrapped `{status:false, message:{errorTypeCode,errorMessage,responseMessage}}`; `toUserError()` in real.ts pulls `message.responseMessage`. Auth errors re-map to existing i18n keys (`auth.error.locked` for 423, else `auth.error.invalid`) via a MockApiError so LoginScreen stays unchanged.
- **WIRE QUIRK — enums are INTEGER indices on some fields.** Verified against the running API: GET /bookings serializes `status`/`source`/`gender`/`language` as ints (status 0=pending,1=confirmed,2=cancelled,3=completed,4=no_show,5=rescheduled; source 0=whatsapp,1=dashboard,2=api,3=walk_in,4=phone_call; language 0=en,1=hi). real.ts maps them before BookingRowSchema.parse. BUT GET /patients `gender` is a STRING ("male") and `preferredLanguage` a STRING ("en") — NOT ints. Always curl-verify a new endpoint's enum representation; it is inconsistent per endpoint.
- **DTO field-name mismatches to watch:** GET /dashboard/summary returns `{liveQueueCount, confirmedTodayCount, todayRevenue, revenueCurrency, noShowRate, asOf}` — NOT the mock's liveQueue/confirmedToday/revenueToday. `noShowRate` is a FRACTION (0..1); StatCards renders `${value}%`, so the adapter ×100. The WhatsApp/walk-in split + activeConversations aren't in the live summary → filled 0.
- **Menu route normalization:** /me/menus returns `/dashboard` for the overview; SPA index is `/` → real.ts `normalizeRoute` maps `/dashboard`→`/`. API also returns routes the SPA lacks (e.g. `/lab` "Lab Tests") — rendered, 404 gracefully. departmentId on DoctorDto is a UUID (not a key), so DEPT_COLOR_KEY always falls back to 'muted' token (graceful, no hex).
- **Tenant comes from the JWT claim**, not a header (X-Tenant-Id ignored server-side; api-client still sends it harmlessly).
- Live verification: Playwright against :5174 (5173 was in use). Login priyanka@apollocare.in/reception → redirect `/`; sidebar renders 10 backend menu items; Bookings/Patients/Doctors show REAL seeded rows (10/10/8) with masked phones + IST slots, zero console errors. Proof shots in `frontend/qa/shots/live/`.
