---
name: webhook-delivery-drain
description: Phase-4 durable async webhook delivery — publish enqueues only, WebhookDeliveryWorker drains; webhook tables are NOT RLS-protected so no SECURITY DEFINER needed; secret_hash registered + encrypted.
metadata:
  type: project
---

Phase-4 reliability slice: webhook delivery moved from SYNCHRONOUS in-request (WebhookPublisher signed+POSTed with Polly retry) to durable async outbox.

Key facts (verified at audit time):
- `platform_api.webhook_subscriptions` / `webhook_deliveries` are NOT RLS-protected. No `ENABLE ROW LEVEL SECURITY` on any `platform_api.*` table anywhere in `database/*.sql`. App role has direct DML grants (`10_roles_grants.sql:59` schema-wide GRANT + `:69-70` DELETE). So the drain runs as PLAIN app-role SQL — no SECURITY DEFINER, unlike the WhatsApp outbox (`outbox_messages` IS RLS-protected at `05_security_hardening.sql:839`). Correct, not a bypass.
- Fan-out: `WebhookRepositories.cs:70 FindDeliverableAsync` — a `tenant_id IS NULL` subscription matches EVERY tenant's event by existing PaaS "subscribe-to-all" design. Pre-existing; this slice did not change it. The drain's `ClaimDueAsync` JOIN to webhook_subscriptions does NOT widen tenant exposure (it only reads url/secret/config for an already-matched delivery row).
- `secret_hash`: registered in `encrypted_fields_registry` (`05_security_hardening.sql:156`), data_class `webhook_signing_secrets`, classification `contact`, legal basis `contract`. NOT PHI/PAN — `contract` basis is appropriate. AES-encrypted (reversible, must be recoverable to sign). `WebhookSigner.SignWithProtected` decrypts to a local var, HMACs, returns only `sha256=<hex>`; plaintext never logged/returned.
- Worker logs delivery_id + event_type + status only — never payload, never secret. Integration-event payloads are IDs-only by convention. No PHI in webhook path.
- Concurrency: `ClaimDueAsync` = `UPDATE ... FROM (SELECT ... FOR UPDATE OF d SKIP LOCKED LIMIT n) ...` flips due rows to 'processing' + writes a lease into `next_retry_at = now + lease`. MarkDelivered/MarkFailed first UPDATE guards `status='processing'` (single-winner). At-least-once; subscribers dedupe on event_id.

KNOWN DEFECT found in this slice (MEDIUM, condition issued — see [[prior-audit-decisions]]):
- In `WebhookDeliveryDrainStore.MarkFailedAsync`/`MarkDeliveredAsync` the SECOND statement (subscription health: consecutive_failures / auto_disabled_at / last_success_at) is UNCONDITIONAL on webhook_id — it does NOT check whether the first (status='processing'-guarded) UPDATE actually matched. On the single-winner LOSER path (a stranded-lease row re-claimed + delivered by worker A while worker B's in-flight call also completes), the loser still bumps/clears subscription health. Worst case: a healthy subscription's consecutive_failures inflated toward AutoDisableThreshold (default 20) by double-counts, or a real success's reset clobbered. Bounded (needs many lease-collisions) and not a security/isolation/PHI issue; it's a reliability/abuse-surface nit. Fix: gate the 2nd UPDATE on the 1st having matched (e.g. CTE `WITH upd AS (UPDATE ... RETURNING webhook_id) UPDATE subscriptions ... FROM upd`).

Auto-disable / DoS: an attacker-controlled subscriber that always fails costs at most (max_retries+1) attempts per delivery then 'abandoned' (not re-claimed), and the subscription auto-disables at AutoDisableThreshold consecutive failures — bounded work. Acceptable.
