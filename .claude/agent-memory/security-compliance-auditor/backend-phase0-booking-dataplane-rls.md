---
name: backend-phase0-booking-dataplane-rls
description: Phase-0 booking data-plane RLS — adds RLS to 5 booking + 5 ai tables, generate_time_slots definer fn, capacity-release. PASS-WITH-CONDITIONS; worker hold-sweep silently no-ops under RLS.
metadata:
  type: project
---

Phase-0 booking data-plane wave (audited 2026-06-28, branch feat/iam-roles-permissions-admin working tree; 112 integration tests GREEN). Adds the long-missing RLS on the OPERATIONAL booking tables + PHI-bearing ai.* tables. VERDICT: PASS WITH CONDITIONS.

**RLS policies added (all mirror the PHI pattern in 05 exactly — `tenant_id = current_tenant_id() OR tenant_id = current_impersonated_tenant()`, super_admin god-flag NOT honored, fail-closed when no GUC):**
- 05_security_hardening.sql:712-748 — bookings, time_slots, slot_holds, opd_tokens (direct tenant_id); booking_status_history via EXISTS parent booking (no tenant_id col, only booking_id).
- 06_ai_services.sql:641-688 — embeddings, ai_predictions, ai_agent_runs, ai_document_extractions (direct tenant_id); ai_agent_steps via EXISTS parent run.
- Bundle docslot_complete.sql parity OK (policies @3095-3122, 3972-3999; fn @2260).
- `FOR ALL` + USING-only (no WITH CHECK) is SAFE: PG reuses USING as WITH CHECK for INSERT/UPDATE, so cross-tenant writes are blocked too. Matches the existing PHI policy style.
- All 10 tables confirmed to carry tenant_id NOT NULL (or correct parent-gate). slot_holds is now CANONICAL in 03 (the old slice-03 app-owned-table condition was honored).

**generate_time_slots (03:988, SECURITY DEFINER, SET search_path=docslot,pg_temp):** derives tenant_id from the doctor row (never caller), range-capped 92d, idempotent ON CONFLICT (doctor_id,slot_date,start_time) DO NOTHING matching the real UNIQUE constraint, fully parameterized/set-based (no injection). Owner = bootstrap superuser ⇒ bypasses RLS by design so the context-less worker can materialize. EXECUTE granted via `GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA docslot` (10:102) — it is the ONLY definer fn in docslot; other 4 are triggers.

**App write-path RLS scoping (verified each runs inside a SET LOCAL app.tenant_id tx):** UnitOfWorkBehavior (commands) + TenantScopeQueryBehavior (queries) both call BeginTenantScopeAsync(currentUser.TenantId). currentUser.TenantId = JWT tenant_id claim, else ITenantScopeOverride (WhatsApp webhook — server-trusted phone_number_id→tenant map, HMAC-verified before side effects, JWT always wins). The status trigger (log_booking_status_change, NOT definer) inserts booking_status_history during the in-scope booking UPDATE → EXISTS parent passes. OpdTokenService + SlotHoldService + BookingRepository all write under the command scope.

**FINDING (MEDIUM, functional regression — NOT security): BookingMaintenanceWorker.ExpireStaleHoldsAsync silently no-ops under RLS.** The singleton worker opens a bare DI scope with NO tenant context; app connects as docslot_app (NOBYPASSRLS, appsettings Username=docslot_app). The sweep `UPDATE docslot.slot_holds SET status='expired' WHERE status='held' AND expires_at<NOW()` now matches 0 rows across ALL tenants (current_tenant_id()=NULL). Stale 'held' rows accumulate unbounded. NOT a correctness bug (HoldAsync's live-hold NOT EXISTS requires expires_at>NOW(), so stale holds don't block new holds) and NOT cross-tenant. Untested (worker never ticks in the test lifetime; only WhatsAppOutboxWebAppFactory removes hosted services). FIX OPTIONS: (a) make ExpireStaleHoldsAsync a SECURITY DEFINER db fn like generate_time_slots; or (b) have the worker iterate tenants setting app.tenant_id per tenant; or (c) REVOKE-and-definer the sweep. The materialize path is fine (goes through the definer fn). Condition for clearance.

**FINDING (LOW/NOTE): generate_time_slots has no caller-tenant assertion (definer-bypass).** A raw `docslot_app` SQL session could call it for any tenant's doctor and pre-materialize availability in that tenant's calendar. Requires arbitrary-SQL-as-docslot_app (already game-over); writes only deterministic 'available' slots into the doctor's OWN (correct) tenant; no read/exfil, no cross-tenant write to attacker tenant, no clobber of booked slots. App surface is fully guarded (ExistsInTenantAsync + RequirePermission docslot.schedule.update). Accepted definer contract; optional hardening = assert v_tenant=current_tenant_id() when a tenant GUC is set.

**FINDING (LOW, test gap): no DB-level cross-tenant RLS test for the 10 new tables** analogous to PhiImpersonationRlsTests/ClinicalPhiTests. HTTP tests pass because the app sets app.tenant_id; they would NOT catch a USING-clause regression to `USING(true)` or a wrong column. Add a docslot_app-connected test proving wrong-tenant rows are invisible. Condition for clearance.

**Cross-tenant guards on schedule/doctor writes (all VERIFIED present):** every handler in GenerateSlotsCommand + ScheduleManagementFeatures (Replace/UpsertOverride/DeleteOverride/UpdateDoctor/DeleteDoctor + the 2 GET queries) calls IDoctorReadService.ExistsInTenantAsync(doctorId, tenantId) FIRST → ForbiddenException. tenant_id always from JWT. Permission-gated docslot.schedule.update / doctor.update / doctor.delete. DoctorReadService SQL filters d.tenant_id=@p0; doctors intentionally NOT RLS-enabled (worker scan) — safe because every doctor read/write is tenant-filtered in code.

**(slot,doctor) guard + capacity (VERIFIED):** HoldAsync conditional INSERT now requires s.doctor_id=@p5 AND s.tenant_id=@p1 AND available AND capacity AND no live hold (one atomic stmt). ReleaseSlotCapacityAsync floors at GREATEST(count-1,0) + reopens 'booked'→'available'. Booking state machine terminal states (Completed/Cancelled/NoShow/Rescheduled all []) ⇒ at most one capacity-free per booking; idempotency-key cache prevents replay double-decrement. Tested: GenerateSlots, CancelBooking_FreesSlotCapacity, CreateBooking_WithMismatchedDoctor_IsRejected (SlotManagementTests).

See [[backend-slice03-docslot-booking]], [[backend-slice03b-clinical-phi]], [[backend-rbac-super-admin-guc]], [[backend-issue3-impersonation-wiring]], [[backend-slice05-security-hardening]].
