---
name: whatsapp-inbound-gotchas
description: Non-obvious schema/architecture facts for the WhatsApp inbound booking flow in mediq (.NET service)
metadata:
  type: project
---

The inbound WhatsApp conversational booking flow lives under `mediq.*/**/Docslot/WhatsApp/`
(controller `WhatsAppController`, command `ProcessInboundWhatsAppMessageCommand`). The inbound handler
ENQUEUES outbound replies into `docslot.outbox_messages` (status 'pending'); the OUTBOUND side is now built —
an `OutboxDrainWorker` (`BackgroundService` in `mediq.Api/Workers/`) claims due rows, sends via
`IWhatsAppSender`, and marks 'sent'/'abandoned'. Sender is selected in `InfrastructureRegistration.AddWhatsAppSender`:
real `MetaWhatsAppSender` (typed HttpClient) when `WhatsApp:AccessToken`+`WhatsApp:GraphBaseUrl` are set, else
the dev `StubWhatsAppSender` (logs + synthetic `wamid.stub.<guid>`, never calls Meta). Claim uses a CTE
`UPDATE ... FROM (SELECT ... FOR UPDATE SKIP LOCKED) RETURNING` to flip 'pending'→'processing' atomically
(scale-out safe). On failure: attempt_count++ then 'abandoned' at max_attempts, else back to 'pending' with
exponential-backoff `next_retry_at`. The worker is cross-tenant (drains every tenant's outbox); each claimed
row still carries its `tenant_id`. NOTE: the due check in the claim uses DB `now()` (not a client-passed
timestamp) to avoid client/DB clock-skew dropping just-enqueued rows.

**To drive a live test:** run the API with `ASPNETCORE_ENVIRONMENT=Development` (else `appsettings.Development.json`
— which holds the `PNID_APOLLO`→tenant map and dev WhatsApp secrets — is NOT loaded and inbound is rejected
"unmapped phone_number_id"). A single signed "hi" enqueues a `booking_prompt` greeting; the worker drains it
to 'sent' within one ~5s poll.

**Why these notes:** several facts here are NOT derivable from reading the code and cost real debugging time.

- **Anonymous-request tenant scoping.** The webhook is `[AllowAnonymous]` and has no JWT, but `CreateBookingCommand`
  + the UoW behavior scope RLS/tenant from `ICurrentUserContext.TenantId` (JWT-claim only). To make an anonymous,
  server-trusted entry point work, two request-scoped override ports were added (Api/Context):
  `ITenantScopeOverride` and `IAmbientIdempotencyKey`. `CurrentUserContext.TenantId` falls back to the override
  ONLY when no JWT tenant claim exists; `IdempotencyContext.Key` falls back to the ambient key ONLY when no
  `Idempotency-Key` header. This is the established pattern for any future trusted-anonymous write path — reuse it
  rather than loosening the JWT-only context.
  **How to apply:** set the override in the trusted edge code (after a server-side lookup), never from a client header.

- **`docslot.wa_contact_profiles.last_relation` CHECK excludes 'self'.** Allowed values are
  `family|friend|neighbour|care_partner|other` (or NULL). Writing 'self' (a "booking for myself" relation) violates
  `wa_contact_profiles_last_relation_check` and rolls back the whole command transaction — which silently undid the
  booking that had already succeeded earlier in the same handler.
  **How to apply:** only persist `last_relation` for the someone-else relations; leave it NULL for self.

- **Column-name surprises (the running DB `docslot_platform`):** `docslot.bookings` has `booked_at` (NOT `created_at`)
  and NO `patient_phone` column on the row used for filtering in tests (filter by tenant + `booked_via`).
  `docslot.departments` has NO `deleted_at`. `wa_message_log.content` is **jsonb** (wrap text as `{"text": ...}`),
  not plain text. `processed_messages` PK is `whatsapp_message_id` (insert ON CONFLICT DO NOTHING; affected==1 ⇒ first sight).

- **Config:** `WhatsAppOptions` binds the `"WhatsApp"` section. Dev defaults in `mediq.Api/appsettings.Development.json`:
  VerifyToken `docslot-verify`, AppSecret `docslot-app-secret`, map `PNID_APOLLO` -> `11111111-1111-1111-1111-111111111111`
  (Apollo Care demo tenant: 7 departments, Cardiology has doctors Anjali Sharma / Rohan Iyer).
  HMAC is `X-Hub-Signature-256: sha256=<hex>` over the EXACT raw body (`Convert.ToHexStringLower`).

See also [[integration-test-db]] — the new WhatsApp test factory follows the same live-DB + per-GUID-tenant pattern.
