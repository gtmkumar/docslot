---
name: clinical-live-wiring
description: Phase-3 slice 4 — clinical/ABDM/consent surface flipped onto the live backend seam, plus medical-history CRUD UI + contextual break-glass.
metadata:
  type: project
---

# Clinical / ABDM live-wiring (Phase-3 slice 4)

The whole clinical surface (prescriptions, lab reports, medical history, ABDM,
consent) now flows through the backend seam (`@/lib/backend`) instead of being
mock-only. Real impls live in `src/lib/backend/real.ts`, registered in
`src/lib/backend/index.ts` behind `USE_REAL_API`; mock parity in
`src/lib/mock/clinical.ts`. `src/features/patients/api.ts` imports the clinical
fns from `@/lib/backend` (was `@/lib/mock`).

**Why:** the fully-built `ClinicalController` was unreachable; the SPA was mock-only.

**How to apply:** when touching any clinical read/write, edit BOTH `real.ts` and
`mock/clinical.ts` so the seam signatures stay identical. The seam picker in
`index.ts` is `USE_REAL_API ? real.fn : mock.fn` — both sides must match.

## Purpose-of-use is now required on ALL clinical reads (incl. lists)
Every clinical GET except `/patients/{id}/consent` sends `X-Purpose-Of-Use`
(`apiFetch({ purposeOfUse })`). The list reads (`listPrescriptions`,
`listLabReports`, `listMedicalHistory`, `listAbdmRecords`) ALL take a
`purpose` arg now and their feature hooks are `enabled` only once a purpose is
declared. Mock list fns call `requirePurpose()` too, so a missing-purpose bug
surfaces in mock mode. A read without the header is a 422 — never let it fire.

## Consent-denied (403) → contextual break-glass → re-fetch
- A consent-denied read 403s (real `ApiError(403)`) / throws `ConsentRequiredError`
  (mock). Detection helper: `isConsentDenied(error)` in
  `features/patients/components/ConsentBlocked.tsx`.
- Detail reads (`usePrescription`/`useLabReport`/`useAbdmRecord`) have
  `retry: false` so a 403 surfaces immediately.
- `ConsentBlocked` renders the affordance, gated on `docslot.medical_access.break_glass`
  via `can()` (NEVER role-in-JSX). Its button opens the `clinicalBreakGlass` panel.
- `ClinicalBreakGlassPanel` POSTs `/security/break-glass`
  `{ patientId, resourceType: 'prescription'|'lab_report'|'medical_history',
  resourceId: uuid|null, justification: string>=10 }`. `useBreakGlass` invalidates
  the whole `['clinical']` query namespace on success → the gated read re-runs and
  now succeeds. The panel carries an optional `reopen: Panel` so a break-glass
  triggered from inside a detail panel restores that panel on success (single-panel
  store would otherwise lose the detail context).
- In mock mode, `breakGlass` flips the seed consent to `granted` so the retry
  demonstrably succeeds; patient `p3` is seeded `clinicalConsent: 'requested'` to
  exercise the path.

## NEW medical-history CRUD UI
- `MedicalHistoryPanel` (one panel, create + edit/retire) off `HistoryTab`
  (Add button gated `docslot.medical_history.create`; per-row Edit pencil gated
  `docslot.medical_history.update`). record_type + severity are Select dropdowns;
  isCritical/isActive are 2-option segmented radiogroups. React 19 Action
  (`useActionState` + `<form action>`), Idempotency-Key per submit.
- Endpoints: POST `/patients/{id}/medical-history` → `{historyId}` (201);
  PUT `/patients/{id}/medical-history/{historyId}` → bool (200; 404 if missing,
  `isActive=false` retires).

## EDIT round-trip (closes a silent data-loss bug)
`MedicalHistoryDto`/`MedicalHistorySchema` now carries the non-encrypted scalars
`severity, icd10Code, startedDate, endedDate` (nullable; C# field order:
historyId, recordType, title, description, severity, icd10Code, startedDate,
endedDate, isActive, isCritical, addedAt). The PUT treats a MISSING body field as
null (= loss), so EDIT must carry ALL of them: the form edits `severity` (pre-filled
via `asSeverity(entry.severity)`); `icd10Code`/`startedDate`/`endedDate` are NOT
edited by the form but are round-tripped from `entry` in the update body so they're
preserved. CREATE intentionally doesn't surface icd10/dates (they default null on a
SPA-created record). Mock seed records carry all four; mock `updateMedicalHistory`
writes exactly the body it receives (mirrors the real PUT).

## PHI / URL discipline
All clinical panels (incl. the new `createHistory`/`editHistory`/`clinicalBreakGlass`)
are in `SlideOverHost`'s `TRANSIENT_SET` → NEVER URL-addressable. The `editHistory`
payload carries the row's decrypted title/description in the in-memory store ONLY.
Decrypted content is rendered (`select-none` on detail panels) but never logged,
never URL-encoded.

## Signature changes worth remembering
- `deliverLabReport(patientId, reportId, idempotencyKey)` — gained patientId (the
  real path is `/patients/{patientId}/lab-reports/{reportId}/deliver`).
- Detail panel store types `prescriptionDetail`/`labReportDetail` gained `patientId`
  (needed for the break-glass context).
