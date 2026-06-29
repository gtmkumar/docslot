---
name: prior-audit-decisions
description: Conditions and verdicts issued per wave, so later waves can be checked against unmet conditions.
metadata:
  type: project
---

Running log of verdicts/conditions I have issued. Check new waves against any OPEN conditions.

## Phase-2 — Hidden-Care-Partner conversion nudge (run_partner_nudge_sweep) — VERDICT: PASS WITH CONDITIONS
Scope: `database/09_chat_identity.sql` (new SECURITY DEFINER fn), `BookingMaintenanceWorker.cs`, `WhatsAppAbstractions.cs` (IPartnerNudgeStore), `WhatsAppRepositories.cs` (PartnerNudgeStore), `DependencyInjection.cs`, new `HiddenPartnerNudgeTests.cs`.
Tenant isolation, DEFINER hygiene, PHI (generic text, no patient identity), anti-spam (cooldown + ≥3 threshold), broker exclusion: all PASS.
OPEN CONDITIONS before any PRODUCTION promotional send (the real Meta path) is enabled:
1. (HIGH) Proactive marketing must go via a Meta PRE-APPROVED TEMPLATE, not free-form text. MetaWhatsAppSender currently sends type:"text" only with no per-intent branching. Until a template-send path keyed by message_intent='partner_nudge' exists, the nudge MUST NOT ship to the real Meta sender. See [[outbox-whatsapp-send-path]].
2. (HIGH) DPDP lawful basis: record/track promotional-outbound opt-in (or documented legitimate-use basis) per recipient, and honor a STOP/opt-out keyword that writes consent_revoked and permanently suppresses nudges. No promotional consent class exists today. See [[consent-dpdp-tables]].
3. (MEDIUM) Outbox scrub: mark_outbox_sent/mark_outbox_failed redact only consent_otp/claim_otp bodies. partner_nudge body is non-secret marketing text, so no redaction needed — but confirm no PHI ever enters the text (today it's clinic display_name only — OK).
These are PASS-WITH-CONDITIONS items: the DEV/stub path (logs only) is safe to merge now; the conditions gate enabling the live Meta promotional send.

## Phase-2 — Payout EXECUTE gateway-outside-txn + idempotency (ExecutePayoutCommandHandler) — VERDICT: PASS WITH CONDITIONS
Scope: `mediq.Application/Cqrs/Behaviors.cs` (new `ISelfManagedTransaction` marker — ONLY ExecutePayoutCommand implements it; UoW behavior skips its ambient tx), `mediq.Application/Features/Commission/PayoutFeatures.cs` (3-phase execute), `mediq.Infrastructure/Commission/CommissionRepositories.cs` (MarkPaid/MarkFailed now return bool via conditional `WHERE status='processing'`), `PayoutGateways.cs` (deterministic DRYRUN ref), `CommissionAbstractions.cs` (PayoutInstruction.IdempotencyKey).
PASS items verified:
- approval≠execution still distinct keys: `commission.payouts.approve` vs `commission.payouts.execute` (CommissionController.cs ~L154/L164).
- RLS preserved per phase: each phase opens its own `BeginTenantScopeAsync` (SET LOCAL app.tenant_id) — including the exception-audit scope. UoW skip does NOT drop RLS.
- single-winner gate sound: Phase-3 wallet/attribution side effects run ONLY inside `if (await MarkPaidAsync(...))` (conditional UPDATE matched one row). Concurrent execute/resume that loses → 0 rows → skips side effects. No double-credit under the interleavings walked.
- 'paid' replay path returns recorded reference, no gateway re-call, no re-credit. Correct.
- leaving 'processing' on gateway EXCEPTION (not 'failed') is the right call — marking failed would release for a fresh disbursement → double-pay. Audited.
- integration event `commission.payout.paid` carries IDs + net_inr only, NO PHI. Audit rows written for every transition (claim implicit, paid, failed, ambiguous-exception).
- deterministic DRYRUN ref is fine: `payment_reference` is NOT unique in schema (07_commission_broker.sql L432; only invoice_number L436 is). `payment_gateway` col exists (L433). No collision risk, no info leak (dry-run only, label says DRY RUN).
OPEN CONDITIONS (pre-prod, before the LIVE gateway adapter is wired — Stub never throws so none bite today):
1. (HIGH, pre-prod) STUCK-'processing' RECONCILIATION GAP: an ambiguous gateway exception leaves the payout 'processing' forever unless a human/worker re-executes. No reconciliation worker, no alert, no `processing`-age monitor exists. Before live go-live: add a reconciler that queries the gateway by IdempotencyKey for aged-'processing' payouts and finalizes, + an alert. Track as a live-gateway go-live gate.
2. (HIGH, pre-prod) LIVE ADAPTER MUST FORWARD IdempotencyKey as the gateway dedupe field (RazorpayX idempotency header / Cashfree transferId). The entire no-double-pay guarantee on resume depends on this. The Stub models it; the real adapter MUST honor it. Gate live go-live on an adapter test proving same-key → same transfer.
See [[payout-finalize-side-effect-scoping]] for the MEDIUM cross-batch concern (pre-existing, not introduced here).

## Phase-4 — Durable async webhook delivery (WebhookDeliveryWorker / WebhookDeliveryDrainStore) — VERDICT: PASS WITH CONDITIONS
Scope: WebhookPublisher (enqueue-only), new WebhookDeliveryDrainStore + WebhookDeliveryWorker + WebhookDeliveryOptions, IWebhookDeliveryDrainStore/ClaimedWebhookDelivery, DI, Program.cs, test host + 2 new tests. NO schema/RBAC/PHI/payout/encryption change — confirmed. See [[webhook-delivery-drain]].
All security gates PASS: no RLS bypass (webhook tables not RLS-protected — verified no ENABLE RLS on platform_api.* anywhere; app role has direct grants), secret stays encrypted (registered `05_security_hardening.sql:156`, SignWithProtected decrypts locally + returns digest only), no PHI in payload/logs, audit chain untouched, no secrets in code/logs, no new permissions, webhook gate N/A (this is OUTBOUND delivery, not the inbound Meta WhatsApp webhook — Meta signature verification gate does not apply here).
CONDITIONS — STATUS (re-verified 2026-06-29, FINAL VERDICT now PASS):
1. (MEDIUM) [CLOSED + re-verified] MarkFailedAsync/MarkDeliveredAsync 2nd statement (subscription health) was unconditional on webhook_id. FIXED: both methods now gate the health UPDATE via `WITH won AS (UPDATE webhook_deliveries ... WHERE delivery_id=@p0 AND status='processing' RETURNING webhook_id) UPDATE webhook_subscriptions s ... FROM won WHERE s.webhook_id=won.webhook_id` — stale loser → won empty → health UPDATE 0 rows. Regression test `Webhook_Stale_SingleWinner_Loser_Does_Not_Perturb_Subscription_Health` (PlatformApiTests.cs:190) delivers success then calls MarkFailedAsync on the now-'success' row, asserts status stays 'success' AND consecutive_failures stays 0 (would bump to 1 without the gate — genuine guard). Independently ran: build 0 err, PlatformApiTests 8/8, full suite 208/208.
2. (INFO/LOW, OPEN — deferred pre-prod) ClaimDueAsync claims status IN ('pending','failed','processing') with elapsed next_retry_at, but partial index idx_webhook_deliveries_pending covers only ('pending','failed') (`02_platform_api.sql:258`). Stranded-'processing' reclaim does a heavier scan. Maintainer tracking as a schema-only index follow-up (deferred to keep this slice schema-free). Not blocking.
3. (INFO, pre-existing, OPEN) `mediq.AppHost` references MessagePack 2.5.192 — NU1903 high-severity advisory GHSA-hv8m-jj95-wg3x. Not introduced by this slice; surfaced during build. Bump when convenient.
