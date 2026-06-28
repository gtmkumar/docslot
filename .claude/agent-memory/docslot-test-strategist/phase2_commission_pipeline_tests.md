---
name: phase2-commission-pipeline-tests
description: Map of Phase-2 commission money-pipeline integration tests — earning/settlement/payout/reversal/dispute/tiered/authz/RLS files + the gotchas that bit them
metadata:
  type: reference
---

Phase-2 commission MONEY-PIPELINE tests added 2026-06 (ledger task #41), 2 new files in
`backend/mediq/tests/mediq.IntegrationTests`:

- **`CommissionPipelineWebAppFactory.cs`**: boots the API as `docslot_app`; seeds a fixture tenant, a broker
  (GST-registered) + wallet, and three users — super_admin (attribution.override + payouts.EXECUTE + all booking
  actions), tenant_admin "finance" (payouts.APPROVE only), tenant_staff "readonly" (attribution.read, NOT
  override). Unlike the older `CommissionWebAppFactory` (seeds bookings already `completed`), this seeds
  COMPLETABLE bookings in `pending` so a test can drive approve→complete to EARN. Cleanup soft-deletes users +
  archives tenant (audit-log FK trap, see [[audit-log-fk-cleanup]]).
- **`CommissionPipelineTests.cs`** (9 cases): earning (attr pending→wallet pending; complete→earned, exercises
  `MarkEarnedForBookingAsync` EF SqlQueryRaw over UPDATE...RETURNING — confirmed WORKS on live PG, not a bug);
  settlement (settle_earned_attributions(0)→ready_to_pay + wallet move); payout dry-run E2E (batch non-empty →
  approve(finance) → execute(super) → paid, ref starts `DRYRUN-`, gateway `stub_dryrun`, wallet
  ready_to_pay→lifetime_paid); DOUBLE-EXECUTE guard
  (`Payout_DoubleExecute_CreditsWalletOnce_AndSecondExecuteIsRejected` — auditor P2 Finding 1 HIGH regression:
  ExecutePayoutCommand atomically claims approved→'processing' via `TryClaimForExecutionAsync` before the
  gateway; a 2nd execute on the same payout sees status 'paid' → BusinessRuleException → 422, wallet
  lifetime_paid unchanged, attributions stay 'paid'); reversal on cancel (pending attr) + on no-show (pending
  attr); dispute clawback (tenant_wins reverses earned + debits, broker_wins does NOT); tiered_table band;
  authz 403 for readonly.
- **`CommissionRlsAndFloorTests.cs`** (5 cases): ₹100 GROSS-floor unit assertion (gross 90 GST-registered →
  net 101.70 > 100 but MeetsMinimum=FALSE because gross<100); commission RLS as `docslot_app` —
  attributions+payouts cross-tenant invisible/insert-blocked, policies exist + not USING(true).

Gotchas that bit the first run (all TEST-input/harness, no backend bugs):
- `commission.attribution_disputes` CHECKs: `raised_by IN ('broker','tenant_staff','platform_audit')` and
  `dispute_reason IN ('incorrect_attribution','duplicate_claim','pndt_violation','patient_dispute',
  'fraud_suspected','commission_calculation_wrong','other')`. Bad values → 23514 → API 500. Dispute resolve
  status enum: `resolved_tenant_wins` clawbacks, `resolved_broker_wins` does NOT.
- `CommissionCalculator.TieredAmount` returns the FIRST matching band; a value equal to a band's `max` stays in
  the LOWER band (`value > max` is strict). For value 500 with bands [0,500]/[500,null], 500 → band1. Use a
  value strictly above the boundary (e.g. ₹800 via a dedicated fee-800 doctor) to hit the open-top band.
- Tiered rule must out-priority the seeded flat ₹200 rule (priority 100) or the engine picks flat.
- broker_wallets is MATERIALIZED state (not source of truth). Tests asserting wallet deltas must
  ResetWalletAsync() at the top (zero all *_inr buckets) — otherwise a prior test's settled ready_to_pay
  residue (attribution row cleaned but wallet bucket left) makes wallet > live-attribution sum and breaks
  `gross == ready_to_pay` assertions. xUnit runs a class's tests sequentially and only this class touches this
  broker, so per-test reset is safe.

Driver patterns: booking action POSTs (`/api/v1/bookings/{id}/approve|complete|cancel|no-show`) REQUIRE an
`Idempotency-Key` header (BookingActionCommand : IRequireIdempotency); commission POSTs do NOT. Drive
settlement via `SELECT commission.settle_earned_attributions(make_interval(secs=>0))` — window 0 settles a
just-earned attribution because earned_at < NOW() advances. Completed bookings are TERMINAL (cannot cancel/
no-show) — earned-attribution reversal only via tenant-wins dispute.

COMPLIANCE-flagged (payout math, attribution/payout RLS, attribution.override authz, MCI/PCPNDT money flow) →
requires security-compliance-auditor sign-off before merge-ready.
