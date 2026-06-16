---
name: slice02-platform-api
description: DocSlot .NET slice 02 (platform_api) — OAuth 2.0 client-credentials, scoped JWTs, dual auth schemes, HMAC webhooks with Polly retry. Builds on slice 01.
metadata:
  type: project
---

Slice 02 (`platform_api`) adds the Platform-as-a-Service layer: OAuth 2.0 for third-party clients + webhook delivery. Builds directly on [[slice01-platform-core]].

**Why:** External integrators consume DocSlot via scoped API tokens; the webhook pipeline is the integration-event seam slice 03 will feed.
**How to apply:** Reuse these patterns for any client-facing API or event-publishing work; do not duplicate the auth/signing machinery.

## Two coexisting auth schemes (key design)
- USER tokens carry `permissions` (RBAC, slice 01) → `[RequirePermission("key")]` → `IPermissionContext`.
- CLIENT tokens carry `scope` (OAuth) → `[RequireScope("docslot.bookings.read")]` → `IScopeContext`.
- Distinguished by a `token_use` JWT claim (`"user"` | `"client"`) set in `JwtTokenService` (new `CreateClientAccessToken`, reuses the same signing key — DRY). `ScopeResolutionMiddleware` resolves a client token's scopes ONCE per request from the AUTHORITATIVE `api_tokens` DB row (not just the claim) AND verifies it's unrevoked/unexpired → fail-closed on revoke before JWT expiry. `PermissionResolutionMiddleware` skips client tokens. Both `perm:` and `scope:` policies are materialized on demand by the single `PermissionPolicyProvider`.

## OAuth flow
`POST /api/v1/oauth/token` (anon, client-credentials): clientId = `client_code`; secret verified against bcrypt `client_secret_hash` via the slice-01 `IPasswordHasher`; client must be active + (first_party OR verified); requested scopes ⊆ granted (`api_client_scopes`); mints scoped JWT; stores SHA-256 hash in `api_tokens`. Bad secret/ineligible → 403 `invalid_client` (uniform, no enumeration); ungranted scope → 422 `invalid_scope`. `POST /api/v1/oauth/revoke` (anon) idempotent by token hash.

## Secret-at-rest rules (auditor-sensitive)
- api_client secret: bcrypt hash (verify only). Plaintext returned ONCE on register/rotate.
- api_token: SHA-256 hex (fits VARCHAR(64)).
- **webhook signing secret: AES-ENCRYPTED, not hashed** — it must be RECOVERABLE to HMAC-sign each delivery. Stored in `webhook_subscriptions.secret_hash` via reused Utilities `EncryptionHelper` (passphrase from `Encryption:Passphrase` config). `IWebhookSigner.ProtectSecret`/`SignWithProtected`. Plaintext returned ONCE on create. This is the one place "secret_hash" column actually holds ciphertext, not a one-way hash — by necessity.

## Webhook pipeline (publish→sign→deliver→retry, outbox)
`IWebhookPublisher.PublishAsync(IntegrationEvent)` fans out to matching active subscriptions (`FindDeliverableAsync` by event_type + tenant), enqueues a `webhook_deliveries` outbox row (event_id = idempotency key), HMAC-SHA256-signs payload (`X-DocSlot-Signature: sha256=<hex>`), POSTs via `IWebhookHttpDispatcher`, and on failure retries with **Polly** (`ResiliencePipelineBuilder<WebhookHttpResult>`, exponential backoff + jitter, MaxRetryAttempts = subscription.max_retries) → dead-letters ('abandoned') on exhaustion. Subscription consecutive_failures auto-disables at 20. `IWebhookHttpDispatcher` is abstracted so tests inject a fake (see `FakeWebhookDispatcher` — `FailFirst(n)` proves retry; asserts HMAC equality). The synthetic `POST /api/v1/webhooks/publish` triggers it in slice 02; slice 03 docslot domain events translate to `IntegrationEvent` at the Application boundary and feed this (the RabbitMQ seam).

## Rate limiting + request logging
`ApiClientRequestLogMiddleware` (after scope resolution): for client-token requests only, enforces per-client `rate_limit_per_minute` (counts recent `api_requests`; 429 + Retry-After on breach) and logs EVERY request to `api_requests` (method/path/status/latency/client/tenant). Gateway still does edge IP rate-limiting + JWT validation (catch-all `/api/v1/{**}` route already covers oauth+public).

## EF / packages
8 tables mapped in `Infrastructure/Persistence/Configurations/PlatformApiConfigurations.cs` (schema `platform_api`, explicit `ToTable(...,"platform_api")` since default schema is `platform`). Postgres `text[]` → `string[]` natively; `jsonb` payload via `HasColumnType("jsonb")`. Added `Polly.Core` to Infrastructure. Management permission: only `platform.api_clients.manage` exists in seed (no granular `platform.api.*` keys) — flagged.

## Verify
`dotnet build mediq.sln` 0 errors (2 warnings = transitive MessagePack/Aspire). `dotnet test` 14/14 (8 slice-01 + 6 slice-02). Schema unmutated (platform_api 8 tables + token_has_scope/token_tenant_id fns intact). Separate `PlatformApiWebAppFactory` seeds an approved first_party client with a known bcrypt secret + 2 granted scopes; does NOT touch the slice-01 fixture.
