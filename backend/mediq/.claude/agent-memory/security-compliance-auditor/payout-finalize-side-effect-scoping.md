---
name: payout-finalize-side-effect-scoping
description: Payout finalize credits the wallet/attributions by RE-QUERYING ready_to_pay per (tenant,broker), not by batch membership — a cross-batch hazard if two open batches coexist.
metadata:
  type: project
---

In `ExecutePayoutCommandHandler` Phase 3 (PayoutFeatures.cs), the single-winner gate (`MarkPaidAsync` conditional UPDATE) correctly prevents double-finalizing the SAME payout. But the side effects it guards are NOT scoped to that payout's attribution set:
- attributions paid via `attributions.ReadyToPayAttributionIdsAsync(tenantId, brokerId)` — re-queried at finalize, ALL `commission_status='ready_to_pay'` rows for the broker.
- wallet credited via `wallets.ApplyPaidAsync(brokerId, payout.GrossAmountInr)` — uses the payout's stored gross.

**Why this is a latent hazard:** Batch creation (`CreatePayoutBatchCommandHandler`) does NOT pin attributions to the payout (it reads ready_to_pay only to count/sum; `payout_id` on attributions is set ONLY at finalize, AttributionRepository L113). And there is NO uniqueness constraint preventing two concurrent OPEN payouts (pending/approved/processing) for one broker — `idx_payouts_tenant_pending` (07_commission_broker.sql L456) is a PLAIN index, not UNIQUE.

So if two batches B1,B2 exist for the same broker and both execute: whichever finalizes second re-queries ready_to_pay and marks whatever remains paid + credits its OWN stored gross. The attribution set and the credited gross can diverge from each batch's intent → over/under-credit or attributions stamped with the wrong payout_id.

**Why I did NOT block on it (this wave):** Pre-existing — this diff did not introduce or worsen the membership model; it only changed transaction boundaries. New attributions settled to ready_to_pay BETWEEN batch-create and finalize were already swept in before. Classified MEDIUM.

**How to apply:** On any FUTURE payout-batch change, require one of: (a) a partial-UNIQUE index `(tenant_id, broker_id) WHERE status IN ('pending','approved','processing')` so only one open batch per broker, OR (b) pin attributions to payout_id at batch-create (claim ready_to_pay→a 'batched' state) and have finalize operate on `WHERE payout_id=@batch`. Until then, the API must serialize batch lifecycle per broker. Re-raise this if a "multiple concurrent batches" feature appears.
