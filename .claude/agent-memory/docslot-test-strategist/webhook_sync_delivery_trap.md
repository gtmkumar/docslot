---
name: webhook-sync-delivery-trap
description: Why any test that POSTs /api/v1/bookings can HANG for minutes — synchronous in-request webhook delivery + leaked platform-wide subscriptions to a dead URL
metadata:
  type: project
---

A test that calls `POST /api/v1/bookings` (the booking-create ENDPOINT, not a direct SQL insert) can HANG for
minutes (manifesting as `TaskCanceledException` / "The client aborted the request" from the in-memory
TestServer, or a bare `NpgsqlException`).

**Why:** booking-create publishes `docslot.booking.created` via `IBookingEventPublisher` →
`WebhookPublisher.PublishAsync`, which delivers SYNCHRONOUSLY IN-REQUEST: for each matching active subscription
it enqueues a `webhook_deliveries` row and `await DeliverWithRetryAsync` does a real HTTP POST to `sub.Url`
wrapped in a Polly retry pipeline (exponential backoff + jitter, up to max_retries, per-call timeout). One
slow/unreachable subscriber stalls the whole booking POST for minutes. Mid-hang, `pg_stat_activity` shows the
`INSERT INTO platform_api.webhook_deliveries` connection `idle in transaction / ClientRead` (server waiting on
the app thread, which is blocked in Polly backoff).

**The aggravator (test-data hygiene):** `PlatformApiWebAppFactory` can LEAK `platform_api.webhook_subscriptions`
rows with `tenant_id IS NULL` (PLATFORM-WIDE → match EVERY tenant's events), `event_types =
{docslot.booking.created}`, `url = https://example.test/hook` (UNREACHABLE). Because tenant_id is NULL,
`FindDeliverableAsync(eventType, anyTenant)` matches them for any tenant — so ANY test that creates a booking
inherits the multi-minute delivery stall. Observed 4 such leaked rows on 2026-06-28.

**How to apply:**
- Tests that need a booking should prefer a DIRECT owner-SQL insert into `docslot.bookings` (like
  `CommissionPipelineTests.SeedCompletableBookingAsync`) over `POST /api/v1/bookings` when they don't
  specifically need the create endpoint — direct insert publishes no event, no webhook stall.
- When a test MUST exercise `POST /api/v1/bookings` (e.g. DirectDiscountTests, DocslotBookingTests), it is at
  the mercy of any active platform-wide webhook subscription. There is currently NO config flag to disable
  inline webhook delivery (unlike the outbox/maintenance workers which `TestHostConfig` disables) — so a
  backend change is needed to make it test-controllable.
- Do NOT "fix" this with client-side retries — a synchronous-delivery stall is not a transient pool blip;
  retrying just re-incurs the multi-minute stall.
- Deleting the leaked rows is the immediate unblock, but the sandbox blocks an agent from DELETEing
  webhook_subscriptions it didn't create (shared multi-tenant state) — needs the user/owner of
  PlatformApiWebAppFactory to clean up + fix that factory's teardown.

**Latent backend risk to flag (auditor/architect):** synchronous in-request webhook delivery means a single
broken subscriber URL degrades booking-creation latency for everyone. Production safety wants delivery DRAINED
ASYNC by the outbox worker (enqueue-only in-request), not awaited inline. Flagged for security-compliance /
dotnet-architect review.
