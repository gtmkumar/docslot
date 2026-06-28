using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Drives the OUTBOUND WhatsApp drain (worker logic) against the live canonical DB. Enqueues
/// <c>docslot.outbox_messages</c> rows directly and runs the real drain store + sender through the factory's
/// <c>DrainOnceAsync</c> (the same claim → send → mark logic the background worker runs).
/// <para>
/// Each test gets its OWN factory instance (separate <see cref="IClassFixture{T}"/>) so the shared
/// controllable-sender flag and the cross-tenant claim batch can't bleed between the success and
/// failure scenarios when the suite runs in parallel.
/// </para>
/// </summary>
public sealed class WhatsAppOutboxDrainSentTests(WhatsAppOutboxWebAppFactory factory)
    : IClassFixture<WhatsAppOutboxWebAppFactory>
{
    [Fact]
    public async Task Drain_Transitions_Pending_To_Sent_With_MessageId()
    {
        _ = factory.CreateClient();
        factory.Sender.FailNext = false;

        var outboxId = await factory.EnqueuePendingAsync("919876500001", "Your appointment is confirmed.");

        await factory.DrainOnceAsync(outboxId);

        var (status, attemptCount, whatsAppMessageId, sentAt) = await factory.ReadAsync(outboxId);
        Assert.Equal("sent", status);
        Assert.NotNull(whatsAppMessageId);
        Assert.StartsWith("wamid.", whatsAppMessageId);
        Assert.NotNull(sentAt);
        Assert.Equal(0, attemptCount);   // a clean first-try success does not bump attempt_count
    }
}

/// <summary>Failure path: a failing sender retries then dead-letters ('abandoned') at max_attempts.</summary>
public sealed class WhatsAppOutboxDrainAbandonTests(WhatsAppOutboxWebAppFactory factory)
    : IClassFixture<WhatsAppOutboxWebAppFactory>
{
    [Fact]
    public async Task Drain_Retries_Then_Abandons_After_MaxAttempts()
    {
        _ = factory.CreateClient();
        factory.Sender.FailNext = true;   // every send fails

        // max_attempts = 2 → after 2 failed attempts the row dead-letters to 'abandoned'.
        var outboxId = await factory.EnqueuePendingAsync("919876500002", "This will never send.", maxAttempts: 2);

        // Attempt 1: claim → fail → attempt_count 1, status back to 'pending'.
        await factory.DrainOnceAsync(outboxId, retryImmediately: true);
        var afterFirst = await factory.ReadAsync(outboxId);
        Assert.Equal("pending", afterFirst.Status);
        Assert.Equal(1, afterFirst.AttemptCount);
        Assert.Null(afterFirst.WhatsAppMessageId);

        // Attempt 2: claim → fail → attempt_count 2 reaches max → 'abandoned'.
        await factory.DrainOnceAsync(outboxId, retryImmediately: true);
        var afterSecond = await factory.ReadAsync(outboxId);
        Assert.Equal("abandoned", afterSecond.Status);
        Assert.Equal(2, afterSecond.AttemptCount);
        Assert.Null(afterSecond.WhatsAppMessageId);
    }
}

/// <summary>
/// Regression for SQLSTATE 42804 ("structure of query does not match function result type") that crashed
/// every <c>OutboxDrainWorker</c> tick: <c>docslot.claim_due_outbox</c> declared
/// <c>RETURNS TABLE(... correlation_id text ...)</c> but returned the <c>varchar</c> <c>correlation_id</c>
/// un-cast, which PostgreSQL's strict result-structure check rejects at that position.
/// <para>
/// The drain tests above deliberately use a test-scoped single-row claim that BYPASSES the definer function,
/// so they never exercised its <c>RETURNS TABLE</c> projection — that's how a 100%-green suite hid a worker
/// that was dead on every tick. This test drives the REAL <see cref="IOutboxDrainStore.ClaimDueAsync"/>
/// (→ <c>docslot.claim_due_outbox</c>) so the bug cannot silently return. It runs inside an EF transaction
/// that is rolled back, so flipping due rows to 'processing' (the function claims cross-tenant) leaves no
/// trace for parallel tests.
/// </para>
/// </summary>
public sealed class WhatsAppOutboxClaimDueFunctionTests(WhatsAppOutboxWebAppFactory factory)
    : IClassFixture<WhatsAppOutboxWebAppFactory>
{
    [Fact]
    public async Task ClaimDue_DefinerFunction_Projects_Varchar_CorrelationId_Without_TypeMismatch()
    {
        _ = factory.CreateClient();
        var correlationId = Guid.NewGuid().ToString();
        var outboxId = await factory.EnqueuePendingAsync(
            "919876500003", "Regression: claim_due_outbox RETURNS TABLE projection.", correlationId: correlationId);

        using var scope = factory.Services.CreateScope();
        // Same scope → the store and this DbContext share one connection, so the store's claim query enlists
        // in the transaction we begin here and is undone by the rollback.
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxDrainStore>();

        await using var tx = await db.Database.BeginTransactionAsync();
        // If correlation_id were returned un-cast (varchar → text-typed column), this throws PostgresException 42804.
        var claimed = await store.ClaimDueAsync(batchSize: 1000, DateTime.UtcNow, default);
        await tx.RollbackAsync();   // discard the cross-tenant 'processing' flip — no side effects

        var mine = claimed.SingleOrDefault(m => m.OutboxId == outboxId);
        Assert.NotNull(mine);                              // the definer function returned our enqueued row
        Assert.Equal(correlationId, mine!.CorrelationId);  // the varchar value projects through intact
    }
}
