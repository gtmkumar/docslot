---
name: backend-patient-timeline
description: Unified patient timeline read endpoint — VETO on consent-bypass-via-aggregation now CLEARED (per-category break-glass gate + parity test landed)
metadata:
  type: project
---

`GET /api/v1/patients/{id}/timeline` (read-only aggregate: prescriptions/lab-reports/vaccinations/paper-import doc cards + booking-derived strip). Audited 2026-07-02. VERDICT: **VETO** (one BLOCKER).

**RESOLUTION (re-review 2026-07-02): BLOCKER CLOSED → timeline gate CLEARED.** Fix rewrote the gate to per-category: `GateAsync(resourceType)` returns allowed when active consent OR a patient-wide grant for THAT type; each category block guarded by `canReadX && xGate.Allowed`; whole-request 403 (`anyPermitted && !anyAccessible`) evaluated BEFORE any PHI decrypt; purpose-of-use now recorded once PER accessed resource type with is_break_glass reflecting only that type's grant (blanket 'patient_timeline' entry gone). Regression test `Timeline_NonConsented_Break_Glass_Grant_For_One_Type_Unlocks_Only_That_Category_Else_403` proves: no-grant→403; prescription-only grant → only prescription chip/items, seeded vaccination stays hidden; purpose_of_use_log has break-glass ONLY on 'prescription', zero on medical_history/lab_report. Suite 390/390. Original BLOCKER writeup retained below for the lesson.

**BLOCKER (ORIGINAL, now fixed) — consent bypass via aggregation (break-glass scope widening).** `PatientTimelineFeatures.cs` lines 46-63: when the patient has NO active consent, the handler looks for a break-glass grant for ANY readable resource type and, if it finds ONE, proceeds to render ALL readable categories. But break-glass grants are strictly resource-type-scoped (`ComplianceServices.cs:312` matches `resource_type = @p3`), and every sibling read requires a grant for its OWN type (prescription read→"prescription", lab_report→"lab_report", medical_history→"medical_history"). So a caller with prescription.read+report.read+medical_history.read who broke glass for "prescription" only, on a non-consented patient, receives decrypted medical_history titles + **external_doctor_name (care-relationship PHI)** + lab-report critical-findings flags — all of which the individual endpoints would 403. Also mis-records: the single 'patient_timeline' purpose-of-use entry attributes the prescription grant's justification to PHI from other categories.

Required fix: gate EACH category by consent OR a grant for THAT category's resource_type (prescription/lab_report/medical_history); whole-request 403 only when no category is accessible; record break-glass in purpose-of-use per the type actually unlocked. Add a no-consent + single-type-grant parity test (tests currently only cover consented patients — `consent_given_at NOW()`, and the omit-categories test uses the consented factory patient, so this path is untested).

**What IS sound (re-verify only the consent gate on re-review):**
- Per-category PERMISSION filter is correct: any-of gate grants entry only; each category included ONLY under its own read permission; `Timeline_Omits_Categories_The_Caller_Cannot_Read` proves omission (chip + items). No privilege widening via the any-of gate.
- No drug-name leak: `CountMedications` emits COUNT only (never names, tolerant of both meds JSON shapes, 0 on malformed). Prescription summary = "N medicines"; doc card = "N records · M medications". Tags = numbers/status/critical flag only. Titles = decrypted diagnosis / history title (in-policy, purpose-gated). Subtitle = doctor name (directory data, not patient PHI; "Dr. Dr." fix uses full_name verbatim — no PHI concern).
- Repo reads tenant-scoped (`WHERE tenant_id=@p0`) + enlist ambient tx for RLS; prescriptions non-draft only; strip = counts only (first booking date Asia/Kolkata + non-cancelled count); doctors/test_catalog LEFT JOINs are non-RLS directory data.
- Purpose required (422 if absent), tenant-linked patient (404), one aggregate purpose-of-use record.
- Frontend `ClinicalTimeline.tsx`/`usePatientTimeline`: sends X-Purpose-Of-Use (apiFetch purposeOfUse), query enabled only with purpose, query key = patientId+purpose (no PHI), default TanStack cache norms (matches siblings).

**Lesson (recurring anti-pattern to check every aggregate/merged read):** an aggregate PHI read must apply the consent/break-glass gate PER underlying resource type, matching the strictness of the individual reads it replaces — never "any one grant unlocks the whole aggregate." Same principle as deny-wins: the aggregate cannot be more permissive than the sum of its parts.

Related: [[backend-paperrx-import]] (external_doctor_name is the leaked field), [[backend-phase3-breakglass-unlock]], [[backend-phase3-medical-history-crud]], [[backend-phase3-labreport-blob]].
