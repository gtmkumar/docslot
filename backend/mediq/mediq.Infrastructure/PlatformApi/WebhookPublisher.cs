using mediq.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace mediq.Infrastructure.PlatformApi;

/// <summary>
/// publish → sign → deliver → retry pipeline. For each integration event it finds matching active
/// subscriptions, creates a <c>webhook_deliveries</c> outbox row (idempotency key = event id), HMAC-signs
/// the payload with the subscription's (decrypted) secret, POSTs to the subscriber, and on failure retries
/// with Polly exponential backoff up to the subscription's <c>max_retries</c>, dead-lettering ('abandoned')
/// after exhaustion. Each delivery's outcome is recorded for the developer portal + abuse detection.
/// </summary>
public sealed class WebhookPublisher(
    IWebhookSubscriptionRepository subscriptions,
    IWebhookDeliveryStore deliveries,
    IWebhookSigner signer,
    IWebhookHttpDispatcher dispatcher,
    IClock clock,
    ILogger<WebhookPublisher> logger)
    : IWebhookPublisher
{
    public async Task<IReadOnlyList<Guid>> PublishAsync(IntegrationEvent evt, CancellationToken ct)
    {
        var matches = await subscriptions.FindDeliverableAsync(evt.EventType, evt.TenantId, ct);
        var created = new List<Guid>(matches.Count);

        foreach (var sub in matches)
        {
            var deliveryId = await deliveries.EnqueueAsync(
                sub.WebhookId, evt.EventType, evt.EventId, evt.PayloadJson, clock.UtcNow, ct);
            created.Add(deliveryId);
            await DeliverWithRetryAsync(sub, deliveryId, evt, ct);
        }

        return created;
    }

    private async Task DeliverWithRetryAsync(
        mediq.Domain.PlatformApi.WebhookSubscription sub, Guid deliveryId, IntegrationEvent evt, CancellationToken ct)
    {
        var signature = signer.SignWithProtected(evt.PayloadJson, sub.SecretHash);
        short attempt = 0;

        // Polly: retry on a failed delivery up to max_retries with exponential backoff + jitter.
        var pipeline = new ResiliencePipelineBuilder<WebhookHttpResult>()
            .AddRetry(new RetryStrategyOptions<WebhookHttpResult>
            {
                ShouldHandle = new PredicateBuilder<WebhookHttpResult>().HandleResult(r => !r.Success),
                MaxRetryAttempts = Math.Max(0, (int)sub.MaxRetries),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(200),
                OnRetry = args =>
                {
                    logger.LogWarning("Webhook {WebhookId} delivery retry {Attempt} (status={Status})",
                        sub.WebhookId, args.AttemptNumber + 1, args.Outcome.Result?.StatusCode);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();

        WebhookHttpResult result = new(false, null, 0, "not-attempted");
        try
        {
            result = await pipeline.ExecuteAsync(async token =>
            {
                attempt++;
                return await dispatcher.PostAsync(sub.Url, evt.PayloadJson, signature, sub.TimeoutSeconds, token);
            }, ct);
        }
        catch (Exception ex)
        {
            result = new WebhookHttpResult(false, null, 0, ex.Message);
        }

        var now = clock.UtcNow;
        if (result.Success)
        {
            await deliveries.MarkSuccessAsync(deliveryId, signature, result.StatusCode ?? 200, result.ElapsedMs, attempt, now, ct);
            await subscriptions.RecordOutcomeAsync(sub.WebhookId, success: true, now, ct);
        }
        else
        {
            // Retries are exhausted here → dead-letter ('abandoned'). A scheduled drainer (later slice) can
            // also re-pick 'failed' rows with a next_retry_at; for slice 02 the synchronous pipeline owns it.
            await deliveries.MarkFailedAsync(
                deliveryId, signature, result.StatusCode, result.Error ?? "delivery failed",
                attempt, nextRetryUtc: null, abandoned: true, now, ct);
            await subscriptions.RecordOutcomeAsync(sub.WebhookId, success: false, now, ct);
        }
    }
}
