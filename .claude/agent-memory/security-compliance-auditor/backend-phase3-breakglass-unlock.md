---
name: backend-phase3-breakglass-unlock
description: Phase-3 slice 1 FR-MED-03 break-glass consent OVERRIDE — break-glass now unlocks consent-denied clinical PHI reads. Audited PASS-WITH-CONDITIONS; purpose_of_use_log not-append-only is the load-bearing carry-forward.
metadata:
  type: project
---

Phase-3 first slice `feat/phase3-breakglass-unlock` (FR-MED-03). Audited 2026-06-29 on branch `main` (uncommitted working tree; the named branch was not yet created). VERDICT: PASS-WITH-CONDITIONS. Highest-sensitivity change so far: break-glass went from theater (only wrote a purpose_of_use_log row; the 6 consent-gated clinical reads 403'd unconditionally) to a real consent OVERRIDE.

## What it does / how it is gated (all verified)
- New table `platform.break_glass_grants` (canonical home `05_security_hardening.sql:714`, section "S7b"; bundle `docslot_complete.sql:3330` byte-identical). Cols: grant_id, user_id→users, tenant_id→tenants NOT NULL, patient_id bare UUID (global patient identity), resource_type VARCHAR CHECK IN (prescription, lab_report, medical_history) — ABDM EXCLUDED, resource_id UUID NULL=patient-wide, justification TEXT NOT NULL, purpose_log_id→purpose_of_use_log, granted_at, expires_at NOT NULL, revoked_at, revoked_by_user_id→users.
- Partial index `idx_break_glass_active (tenant_id, user_id, patient_id, resource_type) WHERE revoked_at IS NULL` — tenant_id leads.
- RLS ENABLED (live `relrowsecurity=t`), policy `tenant_isolation_break_glass_grants` FOR ALL USING `tenant_id=current_tenant_id() OR tenant_id=current_impersonated_tenant()`, NULL WITH CHECK ⇒ USING reused for INSERT/UPDATE WITH CHECK ⇒ cannot insert a grant for a foreign tenant. Owner=gtmkumar, docslot_app is non-owner non-member ⇒ RLS applies (relforcerowsecurity=f is fine, same pattern as the 5 PHI tables).
- Live grants on the table: docslot_app = SELECT, INSERT, UPDATE — **NO DELETE** (soft-delete/revoke discipline). Comes automatically from `10_roles_grants.sql:54` blanket `GRANT SELECT,INSERT,UPDATE ON ALL TABLES IN SCHEMA platform` (10 runs LAST in build order) ⇒ no per-table GRANT line needed in canonical SQL; live/canonical parity holds. The scratchpad apply script was only for the already-initialized live DB (bundle is not idempotent).

## The override path (clean)
- 4 clinical reads now consult `IBreakGlassService.GetActiveGrantAsync(userId, q.TenantId, patientId, resourceType, resourceId?)` ONLY when `patient is null || !HasActiveConsent`; null grant ⇒ still 403. PrescriptionFeatures.cs GetPrescription; ReportAndAbdmFeatures.cs GetLabReport, ListLabReports, ListMedicalHistory.
- resource_id scope semantics (GetActiveGrantAsync SQL): detail read passes specific resourceId → matches patient-wide grant (resource_id IS NULL) OR exact-resource grant; LIST read passes null → matches ONLY a patient-wide grant. So a single-resource grant can NOT unlock a LIST. List handlers pass null. Verified.
- Under a grant the read stamps PurposeOfUseEntry with IsBreakGlass=true + BreakGlassReason=grant.justification at READ time (was hardcoded false,null) ⇒ the ACTUAL access (not just the grant) surfaces in `v_security_review_queue`.
- TTL is server-set const `GrantTtlMinutes=60` via `NOW() + make_interval(mins=>@p8)` — NOT client-controllable. expired/revoked never resolve (`revoked_at IS NULL AND expires_at > NOW()`).

## ABDM two-gate — CANNOT be broken (3 layers)
1. resource_type CHECK constraint excludes abdm_record (DB-enforced, code cannot bypass).
2. BreakGlassValidator whitelists the 3 clinical classes ⇒ abdm_record/patient → 422 (test proves).
3. The 2 ABDM handlers (GetAbdmRecord :307, ListAbdmRecords :176) inject `IAbdmConsentService` (NOT IBreakGlassService) and gate on `consent.HasActiveConsentAsync` → 403 with NO grant fallback. Untouched by the diff (no diff hunk past ~246 in ReportAndAbdmFeatures.cs).

## SoD (grant ≠ review) — REAL at the permission level
- Grant endpoint `[RequirePermission("docslot.medical_access.break_glass")]` — held by roles `doctor` + `super_admin` (is_dangerous=t, scope=tenant).
- NEW revoke endpoint `POST break-glass/{grantId}/revoke` `[RequirePermission("platform.anomalies.review")]` — held by `super_admin` ONLY (NOT doctor). So a doctor who grants CANNOT revoke/self-review. Distinct permissions, distinct roles. Both are pre-existing seeded keys (no new permission introduced).

## Crypto-erasure fail-closed PRESERVED
Decryption path (FieldEncryptionService / KMS) is UNTOUCHED by this diff (grep confirms no crypto file changed). The grant bypasses ONLY the consent check; a destroyed key (DPDP §12) still throws on read even under a grant.

## RLS correctness note (CORRECTS stale slice-03b memory)
`BeginTenantScopeAsync` now opens a REAL tx and `SET LOCAL`s app.tenant_id/user_id/impersonated_tenant/is_super_admin (is_local=true) inside it; `TenantScopeQueryBehavior` wraps reads in that tx. So on reads `db.Database.CurrentTransaction` is non-null and GetActiveGrantAsync's raw NpgsqlCommand correctly enlists it (`cmd.Transaction = CurrentTransaction?.GetDbTransaction()`) ⇒ RLS app.tenant_id applies to the grant lookup. The old "session-scoped set_config, queries outside a tx" model in [[backend-slice03b-clinical-phi]] is SUPERSEDED. Grant lookup also carries explicit tenant_id=q.TenantId predicate (active read tenant) ⇒ defense-in-depth; an impersonation-tenant grant can't cross into the active read tenant (test f-tenant proves 403).

## CARRY-FORWARD CONDITION (HIGH, pre-existing but now load-bearing) — purpose_of_use_log is NOT append-only
`platform.purpose_of_use_log` has `relrowsecurity=f`, NO RLS policy, NO append-only trigger, and docslot_app holds UPDATE (blanket grant). Today safe by CONVENTION: app only ever INSERTs it (IPurposeOfUseWriter + break-glass writer; grep found zero UPDATE paths). BUT after this slice the is_break_glass row is the ONLY record that a real consent-override PHI read happened. A compromised/buggy/future app path could `UPDATE purpose_of_use_log SET reviewed_at=NOW()` or `is_break_glass=false` to silently drop the emergency access from v_security_review_queue. Unlike audit_log/audit_chain (REVOKE UPDATE,DELETE + block_audit_log_mutation trigger), this table has no substrate protection. CONDITION before GA: give purpose_of_use_log the audit_log treatment — REVOKE UPDATE,DELETE from docslot_app + a BEFORE UPDATE/DELETE block trigger (or RLS + append-only). The legit review-close (reviewed_at) should then route through a privileged/definer path. NOT blocking this slice (table predates it; risk is tamper-of-own-trail, not cross-tenant leak), but it is the integrity gap that most undermines break-glass accountability.

## INFO (non-blocking)
- v_security_review_queue does NO tenant filtering in the view; tenant scoping of the review read depends on the read service's explicit predicate + the underlying table RLS. purpose_of_use_log having no RLS means review-queue tenant isolation leans entirely on SecurityReadService's WHERE — confirm that read is tenant-scoped (same posture as prior waves; the read is platform.anomalies.review-gated + actor-masked).
- justification free-text could contain clinician-typed PHI; stored in tenant-scoped non-exported tables (grant + purpose log), shown to reviewers — intentional context, same posture as purpose_of_use_log.justification. Acceptable.
- PatientDetail read gates on HasActiveConsent but serves NO clinical PHI (demographics only) and is intentionally NOT given a break-glass override — correct (break-glass is scoped to clinical record classes).
- Branch `feat/phase3-breakglass-unlock` did not exist at audit time; work was in the working tree on main. Create the branch before commit.

## Tests / build
Build clean (only pre-existing MessagePack NU1903 warning). ClinicalPhiTests + SecurityHardeningTests = 14/14 green. ClinicalPhiTests new case proves: (a) no grant→403, (b)+(c) grant unlocks + DECRYPTS diagnosis + stamps is_break_glass at read time, revoke→403, expired→403, wrong resource_type→403, tenant-B grant→403 for tenant-A read, sanity fresh in-tenant grant→200. SecurityHardeningTests: grant issuance writes scoped active grant row; abdm_record→422.

See [[backend-slice03b-clinical-phi]], [[backend-slice05-security-hardening]], [[backend-issue3-impersonation-wiring]], [[backend-rbac-super-admin-guc]].
