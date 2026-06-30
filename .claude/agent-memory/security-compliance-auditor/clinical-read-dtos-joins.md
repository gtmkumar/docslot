---
name: clinical-read-dtos-joins
description: doctors/test_catalog are tenant-scoped-but-NO-RLS plaintext directory tables; LEFT JOINs onto them from RLS clinical tables must carry an explicit tenant predicate or they leak cross-tenant names
metadata:
  type: project
---

Reviewed branch `fix/clinical-read-dtos` (issues #53/#54), 2026-07.

**docslot.doctors** and **docslot.test_catalog** facts (03_docslot.sql):
- Both carry `tenant_id UUID NOT NULL REFERENCES platform.tenants`. test_catalog is therefore TENANT-SCOPED, not a global catalog.
- `doctors.full_name` (VARCHAR plaintext) and `test_catalog.test_name` (VARCHAR plaintext) are NOT in `platform.encrypted_fields_registry` and NEITHER table is in the RLS ENABLE list (05_security_hardening.sql lines ~670-674 cover only the 5 clinical PHI tables: patient_medical_history, prescriptions, lab_reports, abdm_health_records, drug_alerts). So these two are plaintext directory/catalog data, NOT PHI, NOT RLS-protected.

**Recurring anti-pattern (flag every wave):** because doctors/test_catalog have NO RLS, a SELECT under docslot_app (NOBYPASSRLS) resolves ANY tenant's row. When a tenant-scoped RLS table LEFT JOINs them by id only (`d.doctor_id = p.doctor_id`, `tc.test_id = lr.test_id`) WITHOUT `AND d.tenant_id = p.tenant_id`, and the write path doesn't bind the FK to the tenant (prescriptions.doctor_id / lab_reports.test_id are tenant-blind FKs; IssuePrescriptionValidator/UploadLabReportValidator only NotEmpty-check the id), a caller can persist a row in tenant A referencing tenant B's doctor_id/test_id (a UUID) and read back B's name. **Required fix on these joins: always add the tenant equality predicate.** Affected join sites in ClinicalRepository.cs: GetPrescriptionAsync, ListPrescriptionsAsync, GetLabReportAsync, ListLabReportsAsync. Low severity per-instance (non-PHI string, needs victim UUID + write rights) but it is a genuine cross-tenant surface.

**Validated PHI-egress pattern (issue #54, keep doing):** GetAbdmRecordQueryHandler decrypts the FHIR bundle into a LOCAL var used ONLY by `CountFhirResources()` (returns int; JsonDocument-based; defensive try/catch can't throw or leak), and the DTO carries only `int FhirResourceCount`. The plaintext bundle never enters the DTO / response / log / cache. This is the sanctioned way to derive a metric from PHI without egressing it. `AbdmRecordDto` is constructed in exactly one place; the old `FhirBundleJson` field now lives only on the WRITE-side `PushAbdmRecordRequest`.

**Caching/logging facts (Behaviors.cs):** IdempotencyBehavior implements ONLY `IPipelineBehavior` (commands) — queries are never persisted to the (plaintext) idempotency store, so GET query handlers returning decrypted PHI do NOT need IDoNotCacheResponse. LoggingBehavior runs on both but logs only `typeof(TRequest).Name` + elapsed ms — never the payload or response. (See [[idempotency-cache-sensitive-payload]] for the command-side rule.)
