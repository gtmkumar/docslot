---
name: frontend-ai-triage-phi
description: How the SPA surfaces no-show risk + triage AI without leaking the PHI complaint, and the DPDP purpose-of-use mirror in real.submitTriage. Audited PASS.
metadata:
  type: project
---

Branch `feat/phase4-ai-frontend-surfacing` (Phase-4 AI surfacing slice) — frontend-only wiring of two already-shipped/already-audited backend AI capabilities. Audited PASS. NO schema/RBAC/encryption/backend change; no new permission keys; no role-gating-in-JSX.

**The two surfaces:**
- No-show risk: `GET /bookings/{id}/no-show-risk`, gated `docslot.booking.read`, NO PHI → NO purpose header. `NoShowRiskBadge` (mounted in ManageAppointmentPanel) passes ONLY `booking.id` (a non-PHI booking PK — NOT patient/phone/age) into the query key `['bookings','no-show-risk',id]`. `useNoShowRisk` is `enabled` only while a booking id is present (on-demand, panel-open).
- Triage: `POST /triage`, gated `docslot.booking.create`, advisory (no Idempotency-Key). The `complaint` is PHI. `TriageAssist` (mounted in NewBookingPanel patient step) reads `reason` live via react-hook-form `watch`, passes it ONLY as a `useTriage()` MUTATION variable → request body. Never a query key, never logged, never stored.

**DPDP purpose-of-use mirror (the control to re-verify on any future change):**
- `real.submitTriage` (lib/backend/real.ts ~line 2120): `boundToSubject = Boolean(input.patientId || input.bookingId)`; forwards `purposeOfUse` to `apiFetch({ purposeOfUse })` ONLY when bound. The reception intake call passes NEITHER id → free-text path → no header (matches server's 422 gate, which fires only when patientId/bookingId present and writes purpose_of_use_log).
- Fail-safe if a future bound caller omits `purposeOfUse`: header is absent → SERVER returns 422 → surfaces as isError → honest error/unavailable UI. The client never fabricates a purpose to bypass the server log. Correct posture: server is authoritative, client mirrors.

**Leak vectors verified ABSENT (re-run this grep set on any change here):**
- No `console.*` / debugger / alert in the 4 new files; no localStorage/sessionStorage/indexedDB/gtag/posthog/sentry/analytics/sendBeacon anywhere in the new files.
- No TanStack query persister configured (in-memory cache only) and no ReactQueryDevtools import → mutation vars / cache never reach storage.
- `useTriage` hook has NO onError/onSuccess/toast → the complaint and the ApiError body are never echoed into a toast. Triage error UI uses static i18n only (`triage.error` / `triage.unavailable`), no interpolation of the complaint.
- New-booking form has no draft autosave / storage persister; `reason` defaults to '' in react-hook-form, in-memory only. (The "draft" comments in NewBookingPanel refer to the slot time string, not the complaint.)

**Fail-safe rendering (no fabricated AI output):**
- NoShowRiskBadge: `isError || !data || !data.available || !data.band || probability===null` → muted "Risk unavailable" chip, never an invented band/score.
- TriageAssist / TriageResultBody: `!result.available || !result.urgencyBand` → "Triage unavailable" line; isError → "Triage couldn't run". Never a fabricated assessment.

**Mock honesty:** `lib/mock/ai.ts` is deterministic (FNV-ish hash of booking id for no-show; keyword classifier for triage), labelled `source:'mock-ui'` + `modelName:'mock-noshow-v0'`, and the mock also does NOT log/persist the complaint (consumed only to compute the result, never returned).

**How to apply:** any future change to these surfaces (esp. binding triage to a patient/booking) must (1) keep the complaint out of query keys/logs/storage/toasts, (2) ensure the bound call forwards `purposeOfUse` (or accept the server 422 as the fail-safe), (3) keep `available:false`/error rendering honest. Re-run the leak grep. Builds clean (`npm run build`).
