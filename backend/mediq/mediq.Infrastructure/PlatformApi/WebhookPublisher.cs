using mediq.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace mediq.Infrastructure.PlatformApi;

/// <summary>
/// Publish = ENQUEUE ONLY (durable async outbox). For each integration event it finds the matching active
/// subscriptions and writes a <c>platform_api.webhook_deliveries</c> row in 'pending' (idempotency key = event
/// id) — and returns. It does NOT sign or POST in the request path: the <c>WebhookDeliveryWorker</c> claims due
/// rows out-of-band, signs, delivers, and retries with exponential backoff / dead-letters at max_retries.
/// <para>
/// This is the durable-async replacement for the previous synchronous publish→sign→deliver→retry pipeline,
/// which delivered in-request with Polly: a slow or dead subscriber URL stalled the whole request (e.g. every
/// booking POST) for the retry budget. Now the request returns as soon as the deliveries are enqueued.
/// </para>
/// </summary>
public sealed class WebhookPublisher(
    IIntegrationOutboxStore outbox,
    IWebhookSubscriptionRepository subscriptions,
    IWebhookDeliveryStore deliveries,
    IClock clock,
    ILogger<WebhookPublisher> logger)
    : IWebhookPublisher
{
    public async Task<IReadOnlyList<Guid>> PublishAsync(IntegrationEvent evt, CancellationToken ct)
    {
        // Durable transactional capture FIRST: write the event to the integration-event outbox inside the
        // command's ambient UnitOfWork transaction (atomic with the business write). This runs BEFORE the
        // subscription fan-out so EVERY event is captured — including ones with NO matching webhook
        // subscription, which the fan-out below would otherwise silently drop (the lost-event gap).
        await outbox.RecordAsync(evt, ct);

        var matches = await subscriptions.FindDeliverableAsync(evt.EventType, evt.TenantId, ct);
        var created = new List<Guid>(matches.Count);

        foreach (var sub in matches)
        {
            // Enqueue a 'pending' delivery (next_retry_at NULL → claimed on the worker's next tick). No HTTP here.
            created.Add(await deliveries.EnqueueAsync(
                sub.WebhookId, evt.EventType, evt.EventId, evt.PayloadJson, clock.UtcNow, ct));
        }

        if (created.Count > 0)
            logger.LogDebug("Enqueued {Count} webhook delivery(ies) for event {EventType} ({EventId}).",
                created.Count, evt.EventType, evt.EventId);

        return created;
    }
}
