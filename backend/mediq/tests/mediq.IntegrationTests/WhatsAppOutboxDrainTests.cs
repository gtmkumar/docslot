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
