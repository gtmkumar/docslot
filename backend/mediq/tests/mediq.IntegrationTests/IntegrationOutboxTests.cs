using System.Text.Json;
using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Durable integration-event OUTBOX invariants against the live canonical DB (phase-4 seam). Proves the
/// lost-event gap is closed (capture is atomic with the business write and happens even with ZERO matching
/// webhook subscriptions), the drain store's claim/mark/lease/backoff semantics, and that the NullBus is a
/// clean no-op. The drain worker is disabled suite-wide, so every test drives the stores explicitly.
/// </summary>
public sealed class IntegrationOutboxTests(IntegrationOutboxWebAppFactory factory)
    : IClassFixture<IntegrationOutboxWebAppFactory>
{
    // A synthetic event type that no real webhook subscription is registered for, so the fan-out is
    // guaranteed empty and the test isolates the outbox-capture behavior (the closed lost-event gap).
    private const string UnsubscribedEventType = "docslot.test.outbox_capture";

    private IntegrationEvent NewEvent(Guid? eventId = null, string eventType = UnsubscribedEventType) =>
        new(eventId ?? Guid.CreateVersion7(), eventType, factory.TenantId,
            """{"event_type":"docslot.test.outbox_capture","data":{}}""", $"corr-{Guid.NewGuid():N}", DateTime.UtcNow);

    // (a) Capture survives ZERO matching subscriptions — the closed lost-event gap. ----------------------------
    [Fact]
    public async Task Publish_With_No_Matching_Subscription_Still_Writes_One_Pending_Outbox_Row()
    {
        _ = factory.CreateClient();
        var evt = NewEvent();

        // Drive the REAL WebhookPublisher (the tap). With no subscriptions seeded for this tenant/type, the
        // webhook fan-out writes zero deliveries — but the outbox capture must still record exactly one row.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var publisher = scope.ServiceProvider.GetRequiredService<IWebhookPublisher>();
            await using var tx = await db.Database.BeginTransactionAsync();
            var deliveryIds = await publisher.PublishAsync(evt, default);
            await tx.CommitAsync();
            Assert.Empty(deliveryIds);   // no subscription matched → no webhook delivery enqueued
        }

        Assert.Equal(1, await factory.CountByEventIdAsync(evt.EventId));
        var row = await ReadByEventIdAsync(evt.EventId);
        Assert.Equal("pending", row.Status);
        Assert.Equal(0, row.AttemptCount);
    }

    // (b) Atomicity: a rolled-back business write leaves NO outbox row. ----------------------------------------
    [Fact]
    public async Task Capture_Rolls_Back_With_The_Business_Transaction()
    {
        _ = factory.CreateClient();
        var evt = NewEvent();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var publisher = scope.ServiceProvider.GetRequiredService<IWebhookPublisher>();
            // Capture runs on the SAME connection inside this transaction (the tap is the first statement of
            // PublishAsync). Rolling the transaction back must discard the outbox row too.
            await using var tx = await db.Database.BeginTransactionAsync();
            await publisher.PublishAsync(evt, default);
            await tx.RollbackAsync();
        }

        Assert.Equal(0, await factory.CountByEventIdAsync(evt.EventId));
    }

    // (c) Dedup: the same EventId twice → exactly one row (ON CONFLICT (event_id) DO NOTHING). ------------------
    [Fact]
    public async Task RecordAsync_Is_Idempotent_On_EventId()
    {
        _ = factory.CreateClient();
        var evt = NewEvent();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IIntegrationOutboxStore>();
            await store.RecordAsync(evt, default);
            await store.RecordAsync(evt, default);   // same EventId
        }

        Assert.Equal(1, await factory.CountByEventIdAsync(evt.EventId));
    }

    // (d) ClaimDueAsync flips due rows to 'processing' with a lease and skips not-due rows. ---------------------
    [Fact]
    public async Task ClaimDueAsync_Claims_Due_Leases_It_And_Skips_NotDue()
    {
        _ = factory.CreateClient();
        var dueId = await factory.InsertRowAsync(status: "pending", nextRetryAt: null);
        // A 'failed' row whose backoff is far in the future is NOT due.
        var notDueId = await factory.InsertRowAsync(status: "failed", nextRetryAt: DateTime.UtcNow.AddHours(1), attemptCount: 1);

        var now = DateTime.UtcNow;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutboxDrainStore>();
            // The outbox is a suite-wide SHARED table: every booking/commission command captures a row and the
            // drain worker is OFF in tests, so a backlog of undrained 'pending' rows accumulates — a single
            // small-batch claim can crowd out our seeded row. Drain in large batches until we observe it; the
            // not-due (future next_retry_at) row must NEVER appear in any batch.
            var claimedIds = new HashSet<Guid>();
            var sawNotDue = false;
            for (var i = 0; i < 100 && !claimedIds.Contains(dueId); i++)
            {
                var batch = await store.ClaimDueAsync(batchSize: 500, leaseSeconds: 300, now, default);
                if (batch.Count == 0) break;
                foreach (var c in batch)
                {
                    claimedIds.Add(c.OutboxId);
                    if (c.OutboxId == notDueId) sawNotDue = true;
                }
            }
            Assert.Contains(dueId, claimedIds);
            Assert.False(sawNotDue, "a not-due (future next_retry_at) row must never be claimed");
        }

        var due = await factory.ReadAsync(dueId);
        Assert.Equal("processing", due.Status);
        Assert.NotNull(due.NextRetryAt);                          // a lease watermark was written
        Assert.True(due.NextRetryAt > now);                       // it's in the future (now + leaseSeconds)
        Assert.Equal("failed", (await factory.ReadAsync(notDueId)).Status);   // untouched
    }

    // (e) Single-winner: MarkPublishedAsync on a non-'processing' row is a no-op. -------------------------------
    [Fact]
    public async Task MarkPublishedAsync_On_NonProcessing_Row_Is_A_NoOp()
    {
        _ = factory.CreateClient();
        // Row is still 'pending' (never claimed) → the status='processing' guard makes the mark do nothing.
        var id = await factory.InsertRowAsync(status: "pending");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutboxDrainStore>();
            await store.MarkPublishedAsync(id, DateTime.UtcNow, default);
        }

        var row = await factory.ReadAsync(id);
        Assert.Equal("pending", row.Status);     // unchanged
        Assert.Null(row.PublishedAt);
        Assert.Equal(0, row.AttemptCount);
    }

    // (f) Abandon at MaxRetries vs failed+backoff below it. ----------------------------------------------------
    [Fact]
    public async Task MarkFailedAsync_Backs_Off_Below_Max_And_Abandons_At_Max()
    {
        _ = factory.CreateClient();

        // Below max: attempt_count 0, maxRetries 2 → attempt 1 ≤ 2 → 'failed' with a future backoff.
        var failId = await factory.InsertRowAsync(status: "processing", attemptCount: 0, nextRetryAt: DateTime.UtcNow.AddSeconds(300));
        // At max: attempt_count 2, maxRetries 2 → attempt 3 > 2 → 'abandoned', next_retry_at cleared.
        var abandonId = await factory.InsertRowAsync(status: "processing", attemptCount: 2, nextRetryAt: DateTime.UtcNow.AddSeconds(300));

        var backoff = DateTime.UtcNow.AddSeconds(60);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutboxDrainStore>();
            await store.MarkFailedAsync(failId, "boom", maxRetries: 2, nextRetryUtc: backoff, nowUtc: DateTime.UtcNow, default);
            await store.MarkFailedAsync(abandonId, "boom", maxRetries: 2, nextRetryUtc: backoff, nowUtc: DateTime.UtcNow, default);
        }

        var failed = await factory.ReadAsync(failId);
        Assert.Equal("failed", failed.Status);
        Assert.Equal(1, failed.AttemptCount);
        Assert.NotNull(failed.NextRetryAt);
        Assert.Equal("boom", failed.LastError);

        var abandoned = await factory.ReadAsync(abandonId);
        Assert.Equal("abandoned", abandoned.Status);
        Assert.Equal(3, abandoned.AttemptCount);
        Assert.Null(abandoned.NextRetryAt);     // dead-lettered: no further retry scheduled
    }

    // (g) Lease recovery: a stranded 'processing' row whose lease has elapsed is re-claimable. ------------------
    [Fact]
    public async Task Stranded_Processing_Row_Past_Lease_Is_Reclaimable()
    {
        _ = factory.CreateClient();
        // 'processing' with an ELAPSED lease (next_retry_at in the past) = a crashed worker's stranded row.
        var id = await factory.InsertRowAsync(status: "processing", nextRetryAt: DateTime.UtcNow.AddMinutes(-10));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutboxDrainStore>();
            // Drain past the suite-wide backlog (see ClaimDueAsync_Claims_Due_Leases_It_And_Skips_NotDue) until
            // the stranded row is re-claimed — its past-lease next_retry_at sorts after all NULL 'pending' rows.
            var claimedIds = new HashSet<Guid>();
            for (var i = 0; i < 100 && !claimedIds.Contains(id); i++)
            {
                var batch = await store.ClaimDueAsync(batchSize: 500, leaseSeconds: 300, DateTime.UtcNow, default);
                if (batch.Count == 0) break;
                foreach (var c in batch) claimedIds.Add(c.OutboxId);
            }
            Assert.Contains(id, claimedIds);
        }

        var row = await factory.ReadAsync(id);
        Assert.Equal("processing", row.Status);
        Assert.True(row.NextRetryAt > DateTime.UtcNow);   // a fresh lease was written
    }

    // (h) NullIntegrationEventBus is a clean no-op (the dev/test default). -------------------------------------
    [Fact]
    public async Task NullIntegrationEventBus_Is_A_Clean_NoOp()
    {
        _ = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IIntegrationEventBus>();

        Assert.IsType<mediq.Infrastructure.Messaging.NullIntegrationEventBus>(bus);   // Provider=none default
        // No broker, no throw, no I/O.
        await bus.PublishAsync(Guid.NewGuid(), "docslot.booking.created", factory.TenantId,
            """{"data":{}}""", "corr-x", DateTime.UtcNow, default);
    }

    private async Task<(string Status, int AttemptCount)> ReadByEventIdAsync(Guid eventId)
    {
        await using var conn = new NpgsqlConnection(IntegrationOutboxWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT status, attempt_count FROM platform_api.integration_event_outbox WHERE event_id = @e", conn);
        cmd.Parameters.AddWithValue("e", eventId);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.GetInt16(1));
    }
}

/// <summary>
/// No-PHI architecture guard: the booking + commission integration-event envelopes must serialize ONLY
/// whitelisted ID/scalar keys — NEVER patient name/phone/clinical free-text. This pins the envelope shape so a
/// future publisher change that leaks PHI into the outbox payload (which is non-RLS and broker-replayable) fails
/// CI. It drives the REAL <c>BookingEventPublisher</c> / <c>BrokerEventPublisher</c> (capturing the
/// <c>IntegrationEvent</c> they build) and asserts on the envelope THEY serialize — not a reconstructed copy.
/// </summary>
public sealed class IntegrationOutboxNoPhiTests
{
    // The booking-event envelope (BookingEventPublisher): top-level keys are exactly these.
    private static readonly HashSet<string> AllowedBookingKeys =
        new(StringComparer.Ordinal) { "event_type", "tenant_id", "booking_id", "booking_number", "occurred_at", "data" };

    // The commission-event envelope (mirrors BrokerEventPublisher).
    private static readonly HashSet<string> AllowedCommissionKeys =
        new(StringComparer.Ordinal) { "event_type", "tenant_id", "occurred_at", "data" };

    // PHI/PII (and money-PII) tokens that must NEVER appear anywhere in a serialized envelope (key or value).
    // Extended beyond patient identifiers to the clinical + financial tokens most relevant to THIS broker-
    // replayable, non-RLS outbox (PAN/bank for payout events, ABHA/care-context/complaint for clinical events).
    private static readonly string[] ForbiddenSubstrings =
    [
        "patient_name", "full_name", "phone", "mobile", "email", "address", "diagnosis", "dob",
        "aadhaar", "abha_address", "abha_number", "health_id", "care_context",
        "chief_complaint", "complaint", "prescription_text", "medication",
        "pan", "bank", "account", "ifsc",
    ];

    [Fact]
    public async Task Booking_Publisher_Real_Output_Carries_Only_Whitelisted_Keys_And_No_Phi()
    {
        // Drive the REAL BookingEventPublisher and assert on the envelope IT serializes (not a reconstructed
        // copy), so a future change leaking PHI into the real publisher's payload fails CI.
        var cap = new CapturingWebhookPublisher();
        var pub = new mediq.Infrastructure.Docslot.BookingEventPublisher(cap, new FakeUserContext(), new FixedClock());

        await pub.PublishAsync(
            "docslot.booking.created", Guid.NewGuid(), Guid.NewGuid(), "BKG-2026-06-00042",
            // the IDs-only data a booking handler passes
            new { booking_id = Guid.NewGuid(), patient_id = Guid.NewGuid(), slot_id = Guid.NewGuid(), status = "confirmed" },
            default);

        var json = cap.Captured!.PayloadJson;
        AssertTopLevelKeys(json, AllowedBookingKeys);
        AssertNoPhi(json);
    }

    [Fact]
    public async Task Commission_Publisher_Real_Output_Carries_Only_Whitelisted_Keys_And_No_Phi()
    {
        var cap = new CapturingWebhookPublisher();
        var pub = new mediq.Infrastructure.Commission.BrokerEventPublisher(cap, new FakeUserContext(), new FixedClock());

        await pub.PublishAsync(
            "commission.attribution.created", Guid.NewGuid(),
            new { attribution_id = Guid.NewGuid(), broker_id = Guid.NewGuid(), booking_id = Guid.NewGuid(), commission_inr = 250.00m },
            default);

        var json = cap.Captured!.PayloadJson;
        AssertTopLevelKeys(json, AllowedCommissionKeys);
        AssertNoPhi(json);
    }

    // ---- minimal fakes so the tests exercise the REAL publishers' envelope construction --------------------

    /// <summary>Captures the IntegrationEvent the real publisher builds (the publishers fan through IWebhookPublisher).</summary>
    private sealed class CapturingWebhookPublisher : IWebhookPublisher
    {
        public IntegrationEvent? Captured { get; private set; }
        public Task<IReadOnlyList<Guid>> PublishAsync(IntegrationEvent evt, CancellationToken ct)
        {
            Captured = evt;
            return Task.FromResult<IReadOnlyList<Guid>>([]);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow { get; } = new(2026, 6, 29, 12, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>All members null/false except CorrelationId — the only field the publishers read.</summary>
    private sealed class FakeUserContext : ICurrentUserContext
    {
        public Guid? UserId => null;
        public string? Email => null;
        public Guid? TenantId => null;
        public string? CorrelationId => "corr-test";
        public string? IpAddress => null;
        public string? UserAgent => null;
        public bool IsAuthenticated => false;
        public Guid? BrokerId => null;
        public Guid? ImpersonatedTenantId => null;
    }

    private static void AssertTopLevelKeys(string json, HashSet<string> allowed)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
            Assert.Contains(prop.Name, allowed);
    }

    private static void AssertNoPhi(string json)
    {
        foreach (var forbidden in ForbiddenSubstrings)
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }
}
