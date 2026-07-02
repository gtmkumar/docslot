---
name: backend-paperrx-import
description: Paper-prescription import — intake write-only perm, external_doctor_name encryption, unverified-drafts + clinician verify, attachment blob; clean PASS
metadata:
  type: project
---

Paper-prescription import feature (uncommitted working tree audited 2026-07-02) — VERDICT: clean PASS, no conditions.

**What it is:** front-desk transcribes a patient's paper Rx / self-reported history into `docslot.patient_medical_history` as UNVERIFIED external rows; a clinician later verifies. New columns: source (clinic|paper_prescription|patient_reported), external_doctor_name (encrypted), recorded_date, verified_by_user_id/verified_at, import_batch_id, attachment_* (blob key + metadata).

**Security posture verified:**
- **New perm `docslot.medical_history.intake`** (is_dangerous=true, granted tenant_staff/owner/super_admin). WRITE-ONLY: grants no `.read`. Import endpoint gate = `RequireAnyPermission(create, intake)`; AnyPermission handler uses `permissions.Has` on the resolved deny-wins set (deny override still removes intake → 403). Intake-only staff proven cannot plain-create (403) nor download attachment (403).
- **intake→PHI-read leak: none.** Import response is ids-only (`ImportMedicalHistoryResult(batchId, ids)`), no PHI in audit change_summary (count+source only), errors echo vocab not PHI. `IRequireIdempotency` cache is safe WITHOUT IDoNotCacheResponse because response carries no PHI.
- **Encryption:** title/description/external_doctor_name all encrypted on EVERY write (create + import). external_doctor_name registered in encrypted_fields_registry (medical_history/medical/consent). Attachment BYTES envelope-encrypted (EncryptBytesAsync under `ClinicalFields.HistoryTitle` label) BEFORE blob store → ciphertext at rest (test asserts "PAPER-RX SCAN" absent on disk, keyId present). Same medical_history key seals title+doctor+blob → one DPDP crypto-erasure covers all.
- **Cross-tenant:** import guarded by `patients.IsLinkedToTenantAsync(patientId, tenantId)` (real EF query on PatientTenantLinks) → 404; verify + attachment fetch the row via tenant-filtered GetMedicalHistoryAsync (RLS + WHERE tenant_id) + patient-match → 404 cross-tenant/mismatch; blob GetAsync is tenant-scoped. RLS on table is pre-existing tenant_isolation_medical_history; PhiImpersonationRlsTests exercises A/B/C-tenant isolation (updated to stamp verifier pair for the new clinic-rows-verified CHECK).
- **Verification model:** clinic rows verified-at-creation (schema CHECK chk_history_clinic_rows_verified; Create() stamps verifier=caller/now). External rows verifiable exactly once via single-winner UPDATE (`WHERE source<>'clinic' AND verified_by_user_id IS NULL`) → concurrent double-verify writes exactly one audit row; already-verified/clinic → 422. Pair CHECK chk_history_verify_pair (both stamps or neither).
- **Purpose-of-use:** list read + attachment download both require X-Purpose-Of-Use (422 if absent), record purpose_of_use, and honor consent + break-glass (record-scoped OR patient-wide grant) — identical discipline to lab-report file download.
- **Drug-safety fail-safe (ratified):** ListSafetyHistoryAsync unchanged — selects allergy+medication is_active rows with NO verified filter, so UNVERIFIED imports DO feed screening. Over-alerting beats missing a patient-reported allergy. Test proves imported unverified warfarin trips an interaction alert at finalize.
- **Consent on the WRITE:** import is NOT consent-gated — accepted. Rationale: patient physically handed the document to the clinic; consistent with existing create/update history writes (only reads are consent-gated). DoS bound = 50 records/import + 28M base64 (~20MB) attachment cap.
- Bundle docslot_complete.sql regenerated, no drift (column/CHECKs/registry/perm/grant all present). Suite 383/383 green.

**Non-blocking INFO (not required):** (1) attachment_file_name/mime stored PLAINTEXT + surfaced in list DTO — a filename could embed PHI; behind read+purpose+consent gate so not an open leak, mirrors lab_reports.file_name. (2) blob PutAsync happens inside the command UoW tx → a rollback after PutAsync orphans a (ciphertext, tenant-keyed) blob; storage hygiene only. Consistent with lab-report upload pattern.

Related: [[backend-phase3-labreport-blob]] (same blob/consent discipline), [[backend-phase3-medical-history-crud]] (create/update write path), [[backend-phase3-drug-safety-alerts]] (screening), [[idempotency-cache-sensitive-payload]] (ids-only → no IDoNotCacheResponse needed).
