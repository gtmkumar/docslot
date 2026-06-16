---
name: clinical-records
description: Slice 03b frontend — clinical records UI (prescriptions/lab reports/medical history/ABDM) under patient detail. Purpose-of-use gate, consent, break-glass, and the no-PHI-leakage rules.
metadata:
  type: project
---

Slice 03b (clinical records) frontend shipped the MOST PHI-SENSITIVE surface. Mirrors `ClinicalController` + `mediq.SharedDataModel/Docslot/Clinical/ClinicalDtos.cs` (camelCase).

## Routes & screens (`features/patients/`, lazy)
- `/patients` → `PatientsScreen` (list; replaced the placeholder). Rows show MASKED phone only, link to records.
- `/patients/$patientId/records` → `PatientRecordsScreen` (route id `'/authed/patients/$patientId/records'` for useParams). Header (masked phone + ConsentBadge) → purpose gate → Radix Tabs: Prescriptions | Lab reports | Medical history | ABDM. Tabs in `components/`: `PrescriptionsTab`, `ReportsTab`, `HistoryTab`, `AbdmTab`. Detail/CRUD panels: `PrescriptionDetailPanel`, `IssuePrescriptionPanel`, `LabReportDetailPanel`, `UploadReportPanel`, `AbdmDetailPanel`.

## ACCESS MODEL reflected in the UI (the whole point of this slice)
1. **Purpose-of-use gate** (`components/PurposeGate.tsx`): clinical tabs are LOCKED behind a "Declare purpose & view records" step (picker: treatment/follow_up/emergency/consultation/audit/patient_request/research — the SQL `declared_purpose` enum). The declared purpose lives in `PatientRecordsScreen` COMPONENT STATE (null until declared) → **resets on navigation away** (re-entry must re-declare + re-log). Detail/history/ABDM read hooks are `enabled: Boolean(purpose)` so NO clinical fetch happens before declaration. The purpose flows to reads; `api-client` gained a `purposeOfUse` option → `X-Purpose-Of-Use` header (the mock THROWS without it via `PurposeRequiredError`, mirroring the server 422).
2. **Consent**: `usePatientConsent` drives a prominent `ConsentBadge`. **ABDM is consent-gated**: `AbdmTab` renders NO data when `abdmConsent !== 'granted'` — instead a "no active consent" state with lawful options (request consent / break-glass). `AbdmDetailPanel` surfaces a locked state on `ConsentRequiredError` (mock throws; `useAbdmRecord` has `retry:false`).
3. **Break-glass**: reuses the slice-05 `breakGlass` panel (mandatory justification) from the no-consent state.

## NO-PHI-LEAKAGE rules (verified by audit greps — keep them true)
- **Lists carry NO clinical content**: `PrescriptionListItem`/`LabReportListItem`/`AbdmRecordListItem` are numbers/status/date/type/doctor only. Decrypted detail (`PrescriptionDetail`/`LabReportDetail`) fetched ONLY when a row is opened, with the purpose.
- **Command palette**: zero clinical references (greps clean).
- **URL**: ALL 5 clinical panels are in `SlideOverHost`'s `TRANSIENT_SET` (+ `isUrlPanel` type guard) → NOT URL-addressable; `panelToSearch` returns `{}` for them. A clinical detail (purpose + PHI id) can never be URL-encoded or survive refresh.
- **Toasts**: only non-PHI confirmation keys (e.g. `clinical.rx.issued`), never clinical content.
- **Detail panels** use `select-none` to discourage bulk PHI copy; ABDM FHIR bundle CONTENTS never rendered (only resource count). Patient identity in clinical context = masked phone.

## Gating (real keys, verified in 03_docslot.sql seed)
prescriptions read/create `docslot.prescription.read`/`.create`; reports read/upload/deliver `docslot.report.read`/`.upload`/`.deliver`; history `docslot.medical_history.read`; ABDM read/create `docslot.abdm.records.read`/`.create`; break-glass `docslot.medical_access.break_glass`. All 8 added to the `SIGNED_IN_PERMISSIONS` demo seed. No missing keys.

## Mock contracts (contracts.ts) + adapter (lib/mock/clinical.ts, re-exported from index)
`PurposeOfUse`, `ConsentStatus`, `PatientConsent`, `PrescriptionListItem`/`PrescriptionDetail`/`Medication`/`IssuePrescriptionRequest`/`Result`, `LabReportListItem`/`LabReportDetail`/`LabResultRow`/`UploadLabReportRequest`/`Result`, `MedicalHistory`, `AbdmRecordListItem`/`AbdmRecordDetail`/`PushAbdmRecordResult`. `PrescriptionDto.medicationsJson` / `LabReportDto.structuredResultsJson` are parsed to arrays at the seam (the mock pre-parses). All clinical content is CLEARLY SYNTHETIC. Mutations carry a stable idempotencyKey.

## Missing-GET / contract flags to orchestrator
- **No backend list GETs** for: lab reports (only GET by id exists), ABDM records, medical-history is the only list-ish GET. Built `listLabReports`/`listAbdmRecords` to spec — reconciliation needed.
- **No consent-status GET** + **no deliver endpoint** for lab reports in ClinicalController — `getPatientConsent`/`deliverLabReport` built to spec; `docslot.report.deliver` permission exists but no endpoint. Flagged.
- ABDM `fetch` and consent-`request` are mock toasts (no endpoints surfaced). Download/file actions are buttons pending file endpoints.

## Build
typecheck + build green, no warnings. PatientRecordsScreen ~23kB own chunk; clinical panels split further. `api-client.ts` `purposeOfUse`→`X-Purpose-Of-Use` is the load-bearing seam for the mock→real swap.
