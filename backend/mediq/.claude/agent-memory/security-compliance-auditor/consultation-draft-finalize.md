---
name: consultation-draft-finalize
description: Phase-A prescription consultation composer — new prescriptions columns, gates, and controls; audited PASS.
metadata:
  type: project
---

Consultation draft→finalize composer (docs/PRESCRIPTION_CONSULTATION_PLAN.md Phase A). Audited PASS 2026-07-02.

**Schema (`docslot.prescriptions`, in 03_docslot.sql + bundle):**
- Added `vitals JSONB NOT NULL DEFAULT '{}'` — deliberately UNENCRYPTED standard PHI (locked product decision), NOT in `encrypted_fields_registry`. Purpose-of-use gated on read only.
- Added `drafted_by_user_id`, `finalized_by_user_id`, `finalized_at` (author/signer separation).
- `CHECK chk_prescriptions_signed_rows_have_signer`: status in (finalized/delivered/amended) ⇒ finalized_by + finalized_at NOT NULL. Enforces MCI "signed rows have a signer" in schema.
- Partial unique index `uq_prescriptions_booking_draft ON (booking_id) WHERE status='draft'` (one live draft/booking; get-or-create idempotency).
- Clinical fields (chief_complaints/examination/diagnosis/medications) still encrypted at rest; empty-meds draft seeds a ciphertext of "[]". `investigations` is plaintext JSONB like vitals.
- RLS inherited from `ALTER TABLE docslot.prescriptions ENABLE ROW LEVEL SECURITY` (05_security_hardening.sql:671) — row-level, covers new columns.

**Phase A gates:** all 3 endpoints (POST /consultations, PATCH /consultations/{id}, POST /consultations/{id}/finalize) gated on EXISTING `docslot.prescription.create`. NO new permission added in Phase A. **Phase B** will add `docslot.prescription.draft` (is_phi) + relax create/patch to it — that wave requires my sign-off (per plan §2).

**Controls verified:** finalize derives doctor server-side via `clinical.GetDoctorByUserIdAsync(userId, tenantId)` (docslot.doctors, tenant+is_active+deleted_at scoped), 403 if not a doctor; client can never assert author (FinalizeAsync overwrites doctor_id). Drug-alert override requires override_reason, marks overridden_by/at, never DELETEs. IDoNotCacheResponse on Create+Finalize (PHI). PATCH returns 204. Integration event IDs-only. Purpose-of-use recorded on draft open (Create); read query disabled client-side until purpose declared.

**Legacy Issue/Amend** now stamp `finalized_by_user_id = ctx.UserId` to satisfy the new CHECK.

**Relocation:** `docslot.list_due_noshow_bookings` moved 03→09 (body byte-identical, SECURITY DEFINER + pinned search_path `pg_catalog, docslot, platform` intact, non-PHI columns only). Execute grant covered by blanket `GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA docslot` in 10_roles_grants.sql (runs last). See [[definer-sweep-pattern]].

**INFO (non-blocking):** Finalize decrypts medications for drug screening + returns alert medication names without a fresh PurposeOfUseEntry (only an audit_log entry, Purpose=treatment). Consistent with legacy Issue; purpose was declared at composer open. Consider a purpose entry on the finalize read for completeness.

**Shipped alongside (same wave, all PASS):**
- **Drug-screen parse bug (RESOLVED, was clinical-safety BLOCKER-class).** `DrugSafetyScreeningService.ParseMedications` used a typed `List<MedLine>` deserialize with `dose` typed as string; the structured composer meds shape (`dose` is an OBJECT `{morning,noon,night}`) threw JsonException → catch returned empty → ZERO alerts → screening silently no-op. Fixed to a per-item `JsonDocument` walk handling both legacy (string dose) and structured shapes; per-item tolerance so one odd item never suppresses the rest. WATCH: any future change to medications JSON shape must keep screening parsing in sync — a silent empty-parse disables the whole drug-safety gate.
- **Doctor self-scoped bookings (Phase D §6).** `BookingsController.List`/`Get` now `[RequireAnyPermission("docslot.booking.read","docslot.booking.read_self")]`. Scope enforced server-side in `BookingReadScope.ResolveDoctorFilterAsync` (DocslotQueries.cs): TenantWide precedence → null (reception, full view); else SelfOnly → forced `doctor_id` from `GetDoctorByUserIdAsync` (403 if no doctor profile). ListBookings FORCES `DoctorId=docId`, overriding any client `?doctorId` (no widening); GetBooking returns 404 (not 403) on another doctor's booking (no existence leak). Scope derives from token/perms, never a client param. Reusable pattern for other self-scoped reads.
- **Doctor role grants (03_docslot.sql ~L959).** doctor role = read_self/update_self family + `booking.read_self` (NOT tenant-wide `booking.read` — that's what confines them) + booking.complete + patient.read + medical_history r/c/u + `prescription.create/read/amend` + `report.read` (read-only; report.upload/deliver stay with lab staff = SoD intact). No payout/commission/danger perms. Prescription.draft deliberately absent (Phase B).
