---
name: outbox-whatsapp-send-path
description: How outbound WhatsApp messages flow (outbox → drain → sender); the drain is intent-agnostic and template/opt-in are NOT enforced on send.
metadata:
  type: project
---

The outbound WhatsApp path, end to end:

- Enqueue: writers INSERT into `docslot.outbox_messages` (tenant_id, message_intent, payload jsonb with `{to, text}`, status='pending'). Table def in `database/03_docslot.sql:659`. RLS (tenant isolation) added in `database/05_security_hardening.sql:756-766`.
- Drain: `docslot.claim_due_outbox(p_batch)` (SECURITY DEFINER, `03_docslot.sql:1235`) claims ANY pending row regardless of `message_intent` — FOR UPDATE SKIP LOCKED → 'processing'. Worker = `mediq.Api/Workers/OutboxDrainWorker.cs` (singleton, scope-per-tick).
- Send: `IWhatsAppSender`. `StubWhatsAppSender` (dev, logs only) vs `MetaWhatsAppSender` (real). Both in `mediq.Infrastructure/Docslot/WhatsApp/WhatsAppSenders.cs`. MetaWhatsAppSender selected only when `WhatsApp:AccessToken` + `WhatsApp:GraphBaseUrl` configured.
- Mark: `mark_outbox_sent` / `mark_outbox_failed` (`03_docslot.sql:1264+`). These scrub `payload.text` for `message_intent IN ('consent_otp','claim_otp')` after send / terminal abandon (one-time-code redaction).

**Critical compliance gap (as of Phase-2 nudge wave):**
- `MetaWhatsAppSender.SendAsync` sends `type:"text"` free-form ONLY — NO template send path, NO `message_intent`-based branching. Code comment at WhatsAppSenders.cs:57-58 admits template/interactive "can be added later".
- The drain does not distinguish transactional (reply, within 24h session) from PROACTIVE/marketing intents. So a `partner_nudge` (proactive marketing) would be sent as free-form text, which Meta only permits inside an open 24h customer-service window; outside it Meta REQUIRES a pre-approved template. Free-form proactive marketing → Meta rejection / number quality damage / policy strike.
- **Why:** SECURITY.md threat model line 15 requires "template approval" for WhatsApp. The send path does not enforce it yet.
- **How to apply:** Any feature enqueuing a PROACTIVE (non-reply) outbox intent must be gated on: (a) a template-send path keyed by message_intent, and (b) recipient opt-in. Flag every new proactive intent until the template path exists.

**Mask helper:** `platform.mask_phone()` (`05_security_hardening.sql:773`). Stub sender masks phone in logs.
