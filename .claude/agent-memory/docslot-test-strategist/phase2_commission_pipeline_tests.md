---
name: phase2-commission-pipeline-tests
description: Map of Phase-2 commission integration tests — money pipeline (earn/settle/payout/reversal/dispute/tiered/authz/RLS/double-execute) + direct-booking discount + the gotchas that bit them
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

DIRECT-BOOKING DISCOUNT (Phase-2-remainder, `DirectDiscountTests.cs`, reuses `CommissionPipelineWebAppFactory`
via a shared `[Collection("CommissionPipeline")]` — `CommissionPipelineCollection.cs` — so it shares ONE
tenant/broker with `CommissionPipelineTests` and runs serially with it). A broker-less booking POSTed to
`/api/v1/bookings` with `applyDirectDiscount=true` takes `direct_discount_pct` (CommissionRule default 50) of
the would-be commission (highest-priority rule matching service+value via `CommissionRule.MatchesDirect`, broker
tier/type ignored) as `bookings.direct_discount_inr` + sets `direct_discount_rule_id`. Flat ₹200 rule × 50% on
a ₹500 booking = ₹100. Cases cover: writes ₹100 discount + rule_id; discounted booking REJECTS a later broker
attribution → 422 via `trg_no_attribution_on_discounted`, no row; `applyDirectDiscount=false` → discount stays 0
and stays attribution-ELIGIBLE.

**DISCOUNT BACKEND IS CORRECT** — proven by direct owner-SQL reproduction (GetBookingValue SELECT,
GetActiveRules SELECT, WriteDirectDiscount UPDATE all succeed; ₹100 written) AND my original isolated 4-test
version passed 4/4. **BUT `DirectDiscountTests` is BLOCKED from running green** by the synchronous-webhook hang
(see [[webhook-sync-delivery-trap]]): `POST /bookings` publishes `docslot.booking.created` → `WebhookPublisher`
delivers inline to 4 leaked platform-wide (`tenant_id=NULL`) subscriptions pointing at the unreachable
`https://example.test/hook`, stalling each booking POST for minutes → `TaskCanceledException`/client-abort.
NOT a discount bug and NOT a flake. Unblock needs: clean the leaked `webhook_subscriptions` (sandbox blocks an
agent from DELETEing shared rows it didn't create) AND/OR a backend change to drain webhook delivery async
(currently no flag disables inline delivery, unlike the workers `TestHostConfig` turns off). Booking-create
needs an `Idempotency-Key` header + a bookable slot (`status='available',current_count=0,max_count=1`, seeded
`CURRENT_DATE + 3`).

PROCESS LESSON (self): spawning many overlapping BACKGROUND `dotnet test` runs leaked ~39 testhost procs holding
~45 of 100 PG connections (incl. `idle in transaction` holding locks), which escalated the hangs. Run suite
slices ONE AT A TIME; if instability appears, `pkill -9 -f testhost/vstest` + terminate idle/idle-in-tx backends
before re-running, and confirm `pg_stat_activity` count is ~1 before the next run.

Driver patterns: booking action POSTs (`/api/v1/bookings/{id}/approve|complete|cancel|no-show`) REQUIRE an
`Idempotency-Key` header (BookingActionCommand : IRequireIdempotency); commission POSTs do NOT. Drive
settlement via `SELECT commission.settle_earned_attributions(make_interval(secs=>0))` — window 0 settles a
just-earned attribution because earned_at < NOW() advances. Completed bookings are TERMINAL (cannot cancel/
no-show) — earned-attribution reversal only via tenant-wins dispute.

COMPLIANCE-flagged (payout math, attribution/payout RLS, attribution.override authz, MCI/PCPNDT money flow) →
requires security-compliance-auditor sign-off before merge-ready.
