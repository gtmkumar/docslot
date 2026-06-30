using mediq.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Retention-pruner invariants against the live canonical DB (phase-4). Proves the SWEEP physically removes
/// AGED terminal <c>status='success'</c> rows from the two append-only platform_api operational tables while
/// NEVER touching a non-terminal/retryable row.
/// <para>
/// THE KEY SAFETY TEST is that a <c>'failed'</c> row SURVIVES a prune: <c>'failed'</c> is retryable (the drain
/// re-claims it on its backoff), so deleting it would be the exact data-loss bug a prior slice fixed. We also
/// assert <c>'abandoned'</c> dead-letters survive (forensic) and that a RECENT success (inside the window) is kept.
/// </para>
/// <para>
/// SHARED-HOT-TABLE discipline: the outbox/webhook tables are suite-wide shared, and the prune issues GLOBAL
/// DELETEs of aged success rows — so every assertion is scoped to THIS test's own uniquely-seeded event_ids,
/// never a global table count. (The other suite-captured outbox rows are 'pending' since the drain is OFF, so
/// only this test's seeded old-success rows are eligible to be pruned.) This class shares the
/// <c>[Collection("IntegrationOutbox")]</c> with IntegrationOutboxTests so the two serialize on the shared tables.
/// </para>
/// </summary>
[Collection("IntegrationOutbox")]
public sealed class RetentionPruneTests(IntegrationOutboxWebAppFactory factory)
{
    private IRetentionPruneStore Store(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IRetentionPruneStore>();

    // Integration-event outbox: only the AGED success row is pruned; failed/abandoned/pending/recent-success survive.
    [Fact]
    public async Task PruneSuccessfulIntegrationEvents_Removes_Only_Aged_Success_Rows()
    {
        _ = factory.CreateClient();
        var now = DateTime.UtcNow;

        // FIVE rows, each with a UNIQUE event_id so we can assert per-row without touching the shared backlog.
        var oldSuccessEvt = Guid.NewGuid();
        var recentSuccessEvt = Guid.NewGuid();
        var failedEvt = Guid.NewGuid();
        var abandonedEvt = Guid.NewGuid();
        var pendingEvt = Guid.NewGuid();

        await factory.InsertRowAsync(eventId: oldSuccessEvt, status: "success", publishedAt: now.AddDays(-60));
        await factory.InsertRowAsync(eventId: recentSuccessEvt, status: "success", publishedAt: now.AddDays(-1));
        await factory.InsertRowAsync(eventId: failedEvt, status: "failed", nextRetryAt: now.AddMinutes(5), attemptCount: 1);
        await factory.InsertRowAsync(eventId: abandonedEvt, status: "abandoned", attemptCount: 9);
        await factory.InsertRowAsync(eventId: pendingEvt, status: "pending");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await Store(scope).PruneSuccessfulIntegrationEventsAsync(
                retentionDays: 30, batchSize: 1000, maxBatches: 50, now, default);
        }

        // Aged success → pruned.
        Assert.Equal(0, await factory.CountByEventIdAsync(oldSuccessEvt));
        // Everything else survives. The 'failed' survival is THE safety invariant (retryable, never deleted).
        Assert.Equal(1, await factory.CountByEventIdAsync(recentSuccessEvt));   // inside the window → kept
        Assert.Equal(1, await factory.CountByEventIdAsync(failedEvt));          // retryable → NEVER deleted
        Assert.Equal(1, await factory.CountByEventIdAsync(abandonedEvt));       // dead-letter → kept (forensic)
        Assert.Equal(1, await factory.CountByEventIdAsync(pendingEvt));         // in-flight → kept
    }

    // Webhook deliveries: only the AGED success delivery is pruned; failed/abandoned/recent-success survive.
    [Fact]
    public async Task PruneSuccessfulWebhookDeliveries_Removes_Only_Aged_Success_Rows()
    {
        _ = factory.CreateClient();
        var now = DateTime.UtcNow;

        var oldSuccessEvt = Guid.NewGuid();
        var recentSuccessEvt = Guid.NewGuid();
        var failedEvt = Guid.NewGuid();
        var abandonedEvt = Guid.NewGuid();

        await factory.InsertWebhookDeliveryAsync("success", now.AddDays(-60), oldSuccessEvt);
        await factory.InsertWebhookDeliveryAsync("success", now.AddDays(-1), recentSuccessEvt);
        await factory.InsertWebhookDeliveryAsync("failed", null, failedEvt);
        await factory.InsertWebhookDeliveryAsync("abandoned", null, abandonedEvt);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await Store(scope).PruneSuccessfulWebhookDeliveriesAsync(
                retentionDays: 30, batchSize: 1000, maxBatches: 50, now, default);
        }

        Assert.Equal(0, await factory.CountWebhookDeliveryByEventIdAsync(oldSuccessEvt));   // aged success → pruned
        Assert.Equal(1, await factory.CountWebhookDeliveryByEventIdAsync(recentSuccessEvt)); // inside window → kept
        Assert.Equal(1, await factory.CountWebhookDeliveryByEventIdAsync(failedEvt));        // retryable → NEVER deleted
        Assert.Equal(1, await factory.CountWebhookDeliveryByEventIdAsync(abandonedEvt));     // dead-letter → kept
    }
}
