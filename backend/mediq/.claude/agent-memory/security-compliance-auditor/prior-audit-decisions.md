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
