using mediq.Application.Abstractions;
using mediq.Application.Options;
using Microsoft.Extensions.Options;

namespace mediq.Api.Workers;

/// <summary>
/// Background drain for <c>platform_api.integration_event_outbox</c> — the publish half of the durable
/// integration-event seam. <c>WebhookPublisher</c> CAPTURES every event into the outbox atomically with the
/// business write; this worker publishes them to the message broker out-of-band. On each tick it:
/// <list type="number">
/// <item>claims a batch of DUE rows, atomically flipping each to 'processing' (FOR UPDATE SKIP LOCKED + a
/// visibility lease), so scaled-out instances never double-publish and a crashed worker's row is re-claimable;</item>
/// <item>publishes the event via <see cref="IIntegrationEventBus"/> (the no-op NullBus by default, or the real
/// RabbitMQ adapter when Messaging:Provider=rabbitmq);</item>
/// <item>records the outcome — 'success', or a failure that increments attempt_count and either reschedules with
/// exponential backoff or dead-letters ('abandoned') at MaxRetries.</item>
/// </list>
/// Resilience mirrors <c>WebhookDeliveryWorker</c> / the WhatsApp <c>OutboxDrainWorker</c>: a per-item
/// publish/DB exception is caught and the loop continues; a whole-tick failure is logged and retried next
/// interval; the worker never crashes the host. DEFAULT-OFF (Messaging:DrainWorkerEnabled=false) — the broker
/// and consumer are deferred, so draining is opt-in (the outbox safely accumulates 'pending' rows until then).
/// </summary>
public sealed class IntegrationEventDrainWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MessagingOptions> options,
    ILogger<IntegrationEventDrainWorker> logger) : BackgroundService
{
    private readonly MessagingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds));
        logger.LogInformation(
            "IntegrationEventDrainWorker started (provider={Provider}, poll={PollSeconds}s, batch={BatchSize}, lease={LeaseSeconds}s).",
            _options.Provider, _options.PollSeconds, _options.BatchSize, _options.LeaseSeconds);

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
                logger.LogError(ex, "IntegrationEventDrainWorker tick failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        logger.LogInformation("IntegrationEventDrainWorker stopping.");
    }

    private async Task DrainOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IIntegrationEventOutboxDrainStore>();
        var bus = scope.ServiceProvider.GetRequiredService<IIntegrationEventBus>();

        var batch = await store.ClaimDueAsync(_options.BatchSize, _options.LeaseSeconds, DateTime.UtcNow, ct);
        if (batch.Count == 0)
            return;

        logger.LogDebug("IntegrationEventDrainWorker claimed {Count} due event(s).", batch.Count);
        foreach (var evt in batch)
        {
            ct.ThrowIfCancellationRequested();
            await DeliverAsync(store, bus, evt, ct);
        }
    }

    /// <summary>Publish one claimed row. Any failure (thrown exception) becomes a retry/dead-letter decision; it
    /// never propagates out to abort the batch.</summary>
    private async Task DeliverAsync(
        IIntegrationEventOutboxDrainStore store, IIntegrationEventBus bus, ClaimedIntegrationEvent evt, CancellationToken ct)
    {
        try
        {
            await bus.PublishAsync(
                evt.EventId, evt.EventType, evt.TenantId, evt.PayloadJson, evt.CorrelationId, evt.OccurredAt, ct);
            await store.MarkPublishedAsync(evt.OutboxId, DateTime.UtcNow, ct);
            logger.LogInformation("Integration event {OutboxId} (event={EventType}) published.", evt.OutboxId, evt.EventType);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown mid-publish: leave the row 'processing' — its lease elapses and a later tick re-claims it.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Integration event {OutboxId} publish threw; recording failure.", evt.OutboxId);
            await SafeFailAsync(store, evt, ex.Message, ct);
        }
    }

    private Task FailAsync(IIntegrationEventOutboxDrainStore store, ClaimedIntegrationEvent evt, string error, CancellationToken ct)
    {
        var nextRetryAt = ComputeNextRetry(evt.AttemptCount);
        var willAbandon = evt.AttemptCount + 1 > _options.MaxRetries;
        if (willAbandon)
            logger.LogWarning("Integration event {OutboxId} abandoned after {Attempts} attempt(s): {Error}",
                evt.OutboxId, evt.AttemptCount + 1, error);
        else
            logger.LogWarning("Integration event {OutboxId} failed (attempt {Attempts}/{Max}); retry at {NextRetry:o}: {Error}",
                evt.OutboxId, evt.AttemptCount + 1, _options.MaxRetries, nextRetryAt, error);

        return store.MarkFailedAsync(evt.OutboxId, Truncate(error), _options.MaxRetries, nextRetryAt, DateTime.UtcNow, ct);
    }

    /// <summary>Best-effort failure write — if even this throws (DB down), swallow so the loop survives; the
    /// row's lease elapses and a later tick re-claims it.</summary>
    private async Task SafeFailAsync(IIntegrationEventOutboxDrainStore store, ClaimedIntegrationEvent evt, string error, CancellationToken ct)
    {
        try
        {
            await FailAsync(store, evt, error, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Integration event {OutboxId}: failed to even record the failure (stays 'processing').", evt.OutboxId);
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
