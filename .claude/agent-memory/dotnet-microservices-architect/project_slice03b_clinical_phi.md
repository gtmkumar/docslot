---
name: slice03b-clinical-phi
description: DocSlot .NET slice 03b — least-privilege docslot_app DB role (RLS now enforced), audit-chain concurrency fix, clinical PHI (prescriptions/labs/medical-history/ABDM) with field encryption + RLS + consent + purpose-of-use.
metadata:
  type: project
---

Slice 03b activated the security substrate and shipped clinical PHI. Builds on [[slice05-security-hardening]]. Two BLOCKING prerequisites done first.

**Why:** RLS was decorative (app ran as superuser/bypassrls); the audit chain wasn't concurrency-safe. Both had to be real before serving PHI.

## STEP 0 — least-privilege app role (canonical `database/10_roles_grants.sql`)
`docslot_app` role: **NOSUPERUSER, NOBYPASSRLS, NOCREATEDB, NOCREATEROLE**. Trust auth on localhost (no password; doc says set prod password from secret mgr). Grants: CONNECT + USAGE on platform/platform_api/docslot; SELECT/INSERT/UPDATE on app tables; **narrow DELETE** (bridge/transient tables only — NEVER audit_log/audit_chain); USAGE/SELECT on sequences; EXECUTE on platform functions; ALTER DEFAULT PRIVILEGES for future tables. **Audit append-only is belt-and-suspenders**: REVOKE UPDATE/DELETE on audit_log/audit_chain from docslot_app (grant-layer) + the `block_audit_log_mutation()` guard now CONFINES the escape hatch (`current_user <> 'docslot_app'`) so the app can't bypass via the GUC. The **app connection string (mediq.Api/mediq.AppHost appsettings) now uses `Username=docslot_app`**; test FIXTURES still seed/clean as `gtmkumar` (privileged setup, app-under-test = docslot_app). New file added to `regenerate_bundle.py` (runs LAST, part 10/10).

## STEP 1 — audit-chain concurrency fix (`05_security_hardening.sql`)
`append_to_audit_chain` now takes `pg_advisory_xact_lock(8675309001)` before reading the chain head → concurrent audit_log INSERTs serialize, chain can't fork. **Removed the test `DisableTestParallelization`** (AssemblyInfo.cs); a concurrency test fires 12 parallel prescription POSTs and asserts `verify_audit_chain()` = 0 broken. To repair a historically-broken chain: disable trg_audit_chain, TRUNCATE audit_chain RESTART IDENTITY, re-append in occurred_at order, re-enable.

## STEP 2 — clinical PHI
Tables (schema docslot, all RLS-enabled): prescriptions, lab_reports, patient_medical_history, abdm_health_records (+ abdm_consents read). Domain entities store CIPHERTEXT envelope strings (encrypted via slice-05 `IFieldEncryptionService`); PRX-/RPT- numbers via DB triggers. **jsonb-encrypted columns** (medications, structured_results, fhir_bundle) store the envelope wrapped `to_jsonb(@text)` and read back `#>> '{}'`. **GOTCHA**: EF `SqlQueryRaw<record>` mis-parsed the long base64 envelope (FormatException) — clinical reads use a DIRECT `NpgsqlDataReader` (ordinal access) instead. Domain entities got `FromRow` rehydration factories (private-setter friendly, no reflection).

### Access control per endpoint (defense in depth)
1. **RLS**: app role is NOBYPASSRLS; `app.tenant_id` set per request. Commands: UnitOfWorkBehavior. **Reads: NEW `TenantScopeQueryBehavior`** (query pipeline) — query handlers run outside the command UoW tx, so reads need their own scope-set or RLS returns nothing. `UnitOfWork.SetTenantScopeAsync` now opens the connection + `set_config(...,false)` (SESSION scope, sticks across the scoped DbContext's queries) — LOCAL/true only survives one implicit tx.
2. **Purpose-of-use**: every clinical read requires `X-Purpose-Of-Use` header → logged to purpose_of_use_log (422 if absent).
3. **Consent**: prescriptions/labs/history require `Patient.HasActiveConsent`; ABDM requires an active granted+unexpired `abdm_consents` row (`IAbdmConsentService`) — deny (403) otherwise.
4. **Column policy awareness**: `IAccessPolicyService`/`AccessPolicyService` reads `platform.access_policies` (available; not yet wired into every read — flagged).

### Permission keys (added to canonical `03_docslot.sql` seed — were MISSING)
Added `docslot.prescription.create/read`, `docslot.report.read`, `docslot.medical_history.read` (none existed). Gates: issue→prescription.create, prescription read→prescription.read, report upload→report.upload, report read→report.read, history→medical_history.read, ABDM push→abdm.records.create, ABDM read→abdm.records.read.

### Integration events — NO PHI
`docslot.prescription.issued` + `docslot.report.delivered` → slice-02 webhook publisher with IDs ONLY (prescription_id/report_id/patient_id/booking_id) — proven no PHI in payload.

## Verify
`dotnet build` 0 errors (2 transitive MessagePack warnings). `dotnet test` 30/30 (25 prior + 5 clinical) under PARALLEL execution; chain stays intact (0 broken). Bundle re-validated on fresh DB (115 tables). App runs as docslot_app (super=false/bypassrls=false); RLS actively blocks cross-tenant reads (proven); clinical fields ciphertext at rest (proven).

## Deferred / flags
- access_policies not enforced on every read path (service exists; wire later).
- anomaly_events worker still deferred.
- Slice-02 `/public/patients|bookings` still return empty (no PHI) — consent-bound clinical exposure through public API is a later decision.
- Clinical write paths for medical_history (only read implemented; writes seeded in tests).
- Prod: docslot_app password from secret mgr; RS256/JWKS; KMS-for-real (dev still local_dev provider).
