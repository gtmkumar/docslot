---
name: backend-slice09-whatsapp-webhook
description: Slice 09 inbound WhatsApp webhook — signature verify, anonymous tenant override, dedup, booking via CreateBookingCommand; cleared PASS-WITH-FINDINGS (low/medium only)
metadata:
  type: project
---

`backend/mediq/` inbound WhatsApp Cloud API webhook (Meta). Audited 2026-06-16 — PASS-WITH-FINDINGS, no blockers. New security-sensitive surface: unauthenticated webhook + HMAC + a write path that creates bookings.

**CORRECT (re-verify, don't re-derive):**
- SIGNATURE VERIFY (`WhatsAppSignatureVerifier.cs`): HMAC-SHA256 over raw body keyed by `AppSecret`; `CryptographicOperations.FixedTimeEquals` (constant-time); empty/malformed sig → false. Controller (`WhatsAppController.Receive`) reads raw body FIRST (`Request.EnableBuffering()`, copies Body to MemoryStream, resets Position=0), verifies, and returns 401 BEFORE any parse/dispatch. Order is correct — no side-effect-before-verify. GET handshake compares `hub.verify_token == VerifyToken` (plain ==, low-value token, acceptable) → echoes challenge else 403.
- ANONYMOUS TENANT (the crux): tenant resolved ONLY from server-side `WhatsAppOptions.PhoneNumberIdToTenant[metadata.phone_number_id]`; unmapped → logged + skipped (no row). Never from header/body. Pushed via `ITenantScopeOverride` (request-scoped, `AddScoped` in `ApiServiceExtensions.cs:64`). `CurrentUserContext.TenantId` (`RequestContext.cs:43`) = JWT `tenant_id` claim FIRST, override only as fallback when no JWT claim → an authenticated caller can NEVER be hijacked into another tenant, and the webhook can't escalate past its mapped tenant. RLS picks it up: `UnitOfWorkBehavior`/`TenantScopeQueryBehavior` call `BeginTenantScopeAsync(currentUser.TenantId)` → `SET LOCAL app.tenant_id` (`UnitOfWork.cs:28`).
- IDEMPOTENCY / NO DOUBLE-BOOK (two layers): (1) `ProcessedMessageStore.TryMarkProcessedAsync` INSERT ... ON CONFLICT DO NOTHING on `docslot.processed_messages` PK `whatsapp_message_id` (returns affected==1 only on first sight). This is the FIRST write inside the outer command's UoW tx. (2) The confirm path sets a deterministic ambient Idempotency-Key `wa-{conversationId:N}-{slotId:N}` → `CreateBookingCommand` is `IRequireIdempotency` + durable `DurableIdempotencyStore` (tenant+endpoint+key UNIQUE). Inner CreateBookingCommand reuses the ambient tx (`UnitOfWork.cs:24` reuses CurrentTransaction). So dedup row + booking commit atomically; a Meta redelivery or retried YES cannot create two bookings.
- AUDITED WRITE PATH: booking created ONLY via `commands.Send(new CreateBookingCommand(...))` (handler :239) — reuses slot holds, OPD token, booking-number trigger, status-log trigger, audit, RLS. No raw INSERT into bookings.
- TENANT ISOLATION in wa_* repos (`WhatsAppRepositories.cs`): every SELECT/INSERT/UPDATE/UPSERT filters/sets `tenant_id` (`= @p0`). conversations (tenant_id+phone), wa_contact_profiles (UNIQUE tenant+phone, ON CONFLICT (tenant_id, phone)), wa_message_log (tenant_id), outbox_messages (tenant_id). No cross-tenant leak. Plus RLS-via-SET-LOCAL is active for the whole pipeline.
- PII/PHI + SECRETS: logs only opaque ids (MessageId, ConversationId, phone_number_id=Meta business id NOT patient phone, mode). No raw patient phone/name/chief_complaint/secret logged anywhere in the WhatsApp paths. AppSecret never logged. wa_message_log legitimately stores conversation content (jsonb {text}) per schema.

**FINDINGS (all low/medium — none block):**
- MEDIUM: `processed_messages` has NO tenant_id (PK is global `whatsapp_message_id`). Acceptable because Meta message ids are globally unique and tenant is resolved from phone_number_id before dedup — but it means a message id is consumed process-wide. Not exploitable cross-tenant. Note for canonical-schema review.
- LOW (KNOWN/accepted, dev scope): dev-default `AppSecret="docslot-app-secret"` + `VerifyToken="docslot-verify"` committed in WhatsAppOptions. MUST come from secrets/Aspire in prod (carryover prod-hardening).
- LOW (KNOWN/accepted): outbox drain-worker not implemented (OutboxMessageEnqueuer only enqueues 'pending'). Out of scope for this slice.
- LOW: no replay/timestamp window on the webhook — a captured signed body could be replayed; the processed_messages dedup neutralizes booking-duplication but not log-spam. Tie to the carried webhook-replay prod-hardening item.
- LOW: wa_* operational tables (conversations/wa_message_log/outbox/processed_messages/wa_contact_profiles) are NOT under Postgres RLS (only the 5 clinical PHI tables are, per slice 05). Tenant isolation here rests entirely on app-level `WHERE tenant_id` + SET LOCAL scope. Conversation content is PII (phone, name, free text) but not clinical PHI; defense-in-depth RLS on these would be a hardening win. Track, not block.

GET verify token uses non-constant-time `==` — low value (public-ish handshake token), acceptable.
