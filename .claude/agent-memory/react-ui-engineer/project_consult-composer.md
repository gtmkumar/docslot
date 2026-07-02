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
- Verified: `npx tsc -b` clean, `npm run build` green (ConsultScreen its own chunk), `npm run lint:tokens` clean.

## QA sweep + defect fixes (round 2, browser-verified real + mock)
- **Prescriptions must parse with the RxMedication UNION per-item tolerant, EVERYWHERE.** `PrescriptionDetailSchema.medications` was legacy-only (`z.array(MedicationSchema)`) → structured items rejected the whole detail ("Couldn't load") or fell through empty ("No medicines recorded"). Fixed: `parseMedications(raw)` in contracts.ts (JSON string OR array → per-item `RxMedicationSchema.safeParse`, drops only bad items) is now the single decode path in real `getPrescription`/`parseConsultationDraft` + mock `saveConsultation`. `PrescriptionDetailPanel` renders via `formatMedicationLine` (was hardcoded legacy `dose·frequency·duration`).
- **Past-visits rail** excludes drafts: filter `status!=='draft' && prescriptionId!==currentConsultationId` (real mode: get-or-create returns a `draft`-status prescription that WOULD otherwise self-list). Repeat button disabled when the source has no meds.
- **Template/Repeat dedupe meds by name** (`mergeMeds`) — two overlapping templates (Viral fever + URTI both have Dolo) previously produced duplicate lines.
- **Mock has no persistence across a HARD reload** (in-memory `DRAFTS` Map resets on full page load) — that's expected; real-mode autosave→DB persistence is the real test (owner's vitals survived a real-mode reload). Don't treat a mock hard-reload wipe as a bug.
- Browser-verified in real mode (dr.mehta): all 4 defects, finalize blocked→override→PRX confirmation→read-only, queue Prescribe. Mock (:5199, priyanka): templates+dedup, meds search one-click add (Enter adds top match), steppers, investigations/advice/follow-up add+remove with live preview, safety strip states, doctor qual lookup in preview.
