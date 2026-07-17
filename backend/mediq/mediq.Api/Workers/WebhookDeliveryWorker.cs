using mediq.Application.Abstractions;
using mediq.Application.Options;
using Microsoft.Extensions.Options;

namespace mediq.Api.Workers;

/// <summary>
/// Background drain for <c>platform_api.webhook_deliveries</c> — the delivery half of durable async webhooks.
/// Publishing an integration event only ENQUEUES a 'pending' delivery row (so the request path never blocks on
/// a slow/dead subscriber); this worker delivers. On each tick it:
/// <list type="number">
/// <item>claims a batch of DUE deliveries, atomically flipping each to 'processing' (FOR UPDATE SKIP LOCKED +
/// a visibility lease), so scaled-out instances never double-deliver and a crashed worker's row is re-claimable;</item>
/// <item>HMAC-signs the payload with the subscription's (decrypted) secret and POSTs it to the subscriber;</item>
/// <item>records the outcome — 'success', or a failure that increments attempt_count and either reschedules with
/// exponential backoff or dead-letters ('abandoned') at the subscription's max_retries, auto-disabling a
/// persistently failing subscription.</item>
/// </list>
/// Resilience mirrors the WhatsApp <c>OutboxDrainWorker</c>: a per-delivery send/DB exception is caught and the
/// loop continues; a whole-tick failure is logged and retried next interval; the worker never crashes the host.
/// </summary>
public sealed class WebhookDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WebhookDeliveryOptions> options,
    ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    private readonly WebhookDeliveryOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds));
        logger.LogInformation(
            "WebhookDeliveryWorker started (poll={PollSeconds}s, batch={BatchSize}, lease={LeaseSeconds}s).",
            _options.PollSeconds, _options.BatchSize, _options.LeaseSeconds);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await DrainOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;   // shutdown
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "WebhookDeliveryWorker tick failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        logger.LogInformation("WebhookDeliveryWorker stopping.");
    }

    private async Task DrainOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var claimStore = scope.ServiceProvider.GetRequiredService<IWebhookDeliveryDrainStore>();
        var signer = scope.ServiceProvider.GetRequiredService<IWebhookSigner>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IWebhookHttpDispatcher>();

        var batch = await claimStore.ClaimDueAsync(_options.BatchSize, _options.LeaseSeconds, DateTime.UtcNow, ct);
        if (batch.Count == 0)
            return;

        logger.LogDebug("WebhookDeliveryWorker claimed {Count} due delivery(ies).", batch.Count);

        // Bounded concurrency across the batch (bulkhead): one claimed batch can span many different,
        // unrelated subscriber hosts, so delivering them one at a time let a single slow/dead host stall
        // everyone else's delivery for the rest of the tick. dispatcher's own per-host circuit breaker (see
        // WebhookHttpDispatcher) additionally fast-fails a known-broken host instead of burning its timeout on
        // every attempt. Each concurrent unit gets its OWN scope (hence its own DbContext) for the
        // delivered/failed write — DbContext is not thread-safe, so writes can never share the outer scope's
        // instance; signer/dispatcher ARE safe to share (stateless / internally thread-safe).
        await Parallel.ForEachAsync(
            batch,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxDegreeOfParallelism), CancellationToken = ct },
            async (delivery, itemCt) =>
            {
                await using var itemScope = scopeFactory.CreateAsyncScope();
                var store = itemScope.ServiceProvider.GetRequiredService<IWebhookDeliveryDrainStore>();
                await DeliverAsync(store, signer, dispatcher, delivery, itemCt);
            });
    }

    /// <summary>Deliver one claimed row. Any failure (returned result OR thrown exception) becomes a retry/
    /// dead-letter decision; it never propagates out to abort the batch.</summary>
    private async Task DeliverAsync(
        IWebhookDeliveryDrainStore store, IWebhookSigner signer, IWebhookHttpDispatcher dispatcher,
        ClaimedWebhookDelivery d, CancellationToken ct)
    {
        try
        {
            // Decrypt the stored secret and HMAC-sign in one step (plaintext never lives beyond the call).
            var signature = signer.SignWithProtected(d.PayloadJson, d.SecretHash);
            var result = await dispatcher.PostAsync(d.Url, d.PayloadJson, signature, d.TimeoutSeconds, ct);

            if (result.Success)
            {
                await store.MarkDeliveredAsync(d.DeliveryId, signature, result.StatusCode ?? 200, result.ElapsedMs, DateTime.UtcNow, ct);
                logger.LogInformation("Webhook delivery {DeliveryId} (event={EventType}) delivered → {Status}",
                    d.DeliveryId, d.EventType, result.StatusCode);
            }
            else
            {
                await FailAsync(store, d, signature, result.StatusCode, result.Error ?? "delivery failed", ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown mid-delivery: leave the row 'processing' — its lease elapses and a later tick re-claims it.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Webhook delivery {DeliveryId} threw; recording failure.", d.DeliveryId);
            await SafeFailAsync(store, d, null, ex.Message, ct);
        }
    }

    private Task FailAsync(IWebhookDeliveryDrainStore store, ClaimedWebhookDelivery d, string? signature, int? statusCode, string error, CancellationToken ct)
    {
        var nextRetryAt = ComputeNextRetry(d.AttemptCount);
        var willAbandon = d.AttemptCount + 1 > d.MaxRetries;   // max_retries = retries beyond the first attempt
        if (willAbandon)
            logger.LogWarning("Webhook delivery {DeliveryId} abandoned after {Attempts} attempt(s): {Error}",
                d.DeliveryId, d.AttemptCount + 1, error);
        else
            logger.LogWarning("Webhook delivery {DeliveryId} failed (attempt {Attempts}/{Max}); retry at {NextRetry:o}: {Error}",
                d.DeliveryId, d.AttemptCount + 1, d.MaxRetries, nextRetryAt, error);

        return store.MarkFailedAsync(
            d.DeliveryId, signature ?? string.Empty, statusCode, Truncate(error),
            d.MaxRetries, _options.AutoDisableThreshold, nextRetryAt, DateTime.UtcNow, ct);
    }

    /// <summary>Best-effort failure write — if even this throws (DB down), swallow so the loop survives; the
    /// row's lease elapses and a later tick re-claims it.</summary>
    private async Task SafeFailAsync(IWebhookDeliveryDrainStore store, ClaimedWebhookDelivery d, string? signature, string error, CancellationToken ct)
    {
        try
        {
            await FailAsync(store, d, signature, null, error, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook delivery {DeliveryId}: failed to even record the failure (stays 'processing').", d.DeliveryId);
        }
    }

    /// <summary>Exponential backoff: base * 2^attemptCount seconds from now, capped. attemptCount is the
    /// pre-increment count (0 on the first failure → base delay).</summary>
    private DateTime ComputeNextRetry(int attemptCount)
    {
        var baseSeconds = Math.Max(1, _options.BackoffBaseSeconds);
        var capSeconds = Math.Max(baseSeconds, _options.BackoffMaxSeconds);
        var exponent = Math.Min(attemptCount, 16);
        var delaySeconds = Math.Min((long)baseSeconds * (1L << exponent), capSeconds);
        return DateTime.UtcNow.AddSeconds(delaySeconds);
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
