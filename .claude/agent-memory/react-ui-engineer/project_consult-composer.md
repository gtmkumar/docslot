---
name: consult-composer
description: Phase-A consultation composer (/consult/$bookingId) — doctor Rx-writing surface, seam fns, structured meds contract, history rail, and the IssuePrescriptionPanel removal.
metadata:
  type: project
---

Phase A of docs/PRESCRIPTION_CONSULTATION_PLAN.md — the doctor prescription-writing composer.

**Why:** owner wants a fast, chips-over-typing Rx composer matching docs/proto_type_prescreption_screens/ (6 approved PNGs), with patient history at the doctor's fingertips.
**How to apply:** extend here for Phase B (draft-only intake role via `docslot.prescription.draft`) / Phase C (per-doctor `docslot.rx_templates`) / Phase D (doctor-scoped Overview + patient list).

- Feature lives in `frontend/src/features/consult/` (ConsultScreen.tsx + `components/` + `api.ts` + `constants.ts` + `model.ts`). Full-screen two-pane route inside AppShell.
- Wire contract is FIXED (see [[frontend-contract-surface]] and `.agents/memory/api-contracts.md` "Consultation composer"): POST `/consultations` (get-or-create, 200 draft), PATCH `/consultations/{id}` (204 autosave), POST `/consultations/{id}/finalize` (FinalizeConsultationResult; `finalized:false` = blocked by high/critical drug alerts → override reason → retry). `medicationsJson` is a STRING parsed to the structured `medications` array at the seam.
- Structured meds = `StructuredMedicationSchema` (dose {morning,noon,night} + sos/weekly/timing/durationDays/instructions); `RxMedicationSchema` is a union keeping the legacy `{name,dose,frequency,duration}` displayable via `formatMedicationLine(med,t)`.
- React 19 idioms: `useActionState` for the finalize form (idle→blocked→done), debounced-effect autosave (~800ms). React Compiler on — no manual memo.
- Removed the buggy `IssuePrescriptionPanel` (hardcoded doctorId/bookingId). Patient-screen "New prescription" + queue-row "Prescribe" now route to the composer for a REAL booking, gated on `docslot.prescription.create`.
- `PurposeGate`/`PurposeBanner` are now shared at `components/ui/PurposeGate.tsx` (patients + consult).
- Verified: `npx tsc -b` clean, `npm run build` green (ConsultScreen its own chunk), `npm run lint:tokens` clean. Browser flow verification left to the integrated pass.
