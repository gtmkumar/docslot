using mediq.Application.Abstractions;
using mediq.Application.Options;
using Microsoft.Extensions.Options;

namespace mediq.Api.Workers;

/// <summary>
/// Background drain for <c>docslot.outbox_messages</c> — the outbound half of the WhatsApp flow. The inbound
/// handler only ENQUEUES (status 'pending'); this worker delivers. On a configurable interval it:
/// <list type="number">
/// <item>claims a batch of DUE messages, atomically flipping each 'pending' → 'processing' (FOR UPDATE SKIP
/// LOCKED), so two workers / scaled-out instances never double-send;</item>
/// <item>sends each via <see cref="IWhatsAppSender"/> (stub in dev, real Meta sender when configured);</item>
/// <item>persists the outcome — 'sent' with the provider id, or a failure that increments attempt_count and
/// either reschedules with exponential backoff or dead-letters ('abandoned') at max_attempts.</item>
/// </list>
/// Resilience: a send/DB exception for one message is caught and logged; the loop continues to the next
/// message and the next tick. The whole worker never crashes the host on a transient failure.
/// <para>
/// Scoping: the worker is a singleton hosted service, so it opens a DI SCOPE per tick to resolve the scoped
/// <see cref="IOutboxDrainStore"/> / <see cref="IWhatsAppSender"/> (and their scoped DbContext). Each claimed
/// message carries its own <c>tenant_id</c>; the drain store reads/writes by <c>outbox_id</c> and surfaces
/// the tenant on the projected message for downstream attribution.
/// </para>
/// </summary>
public sealed class OutboxDrainWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WhatsAppOptions> options,
    ILogger<OutboxDrainWorker> logger) : BackgroundService
{
    private readonly WhatsAppOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.OutboxPollSeconds));
        logger.LogInformation(
            "OutboxDrainWorker started (poll={PollSeconds}s, batch={BatchSize}).",
            _options.OutboxPollSeconds, _options.OutboxBatchSize);

        using var timer = new PeriodicTimer(interval);

        // Drain once immediately, then on every tick. PeriodicTimer.WaitForNextTickAsync returns false only
        // on cancellation, which cleanly exits the loop on shutdown.
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
                // A whole-tick failure (e.g. transient DB outage on claim) must not kill the worker.
                logger.LogError(ex, "OutboxDrainWorker tick failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        logger.LogInformation("OutboxDrainWorker stopping.");
    }

    /// <summary>One drain pass: claim a batch and send each claimed message, isolating per-message failures.</summary>
    private async Task DrainOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxDrainStore>();
        var sender = scope.ServiceProvider.GetRequiredService<IWhatsAppSender>();

        var nowUtc = DateTime.UtcNow;
        var batch = await store.ClaimDueAsync(_options.OutboxBatchSize, nowUtc, ct);
        if (batch.Count == 0)
            return;

        logger.LogDebug("OutboxDrainWorker claimed {Count} due message(s).", batch.Count);

        foreach (var message in batch)
        {
            ct.ThrowIfCancellationRequested();
            await DeliverAsync(store, sender, message, ct);
        }
    }

    /// <summary>
    /// Deliver one claimed message. Any send failure (returned result OR thrown exception) is converted into a
    /// retry/dead-letter decision; it never propagates out to abort the batch.
    /// </summary>
    private async Task DeliverAsync(
        IOutboxDrainStore store, IWhatsAppSender sender, OutboundMessage message, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message.ToPhone))
            {
                // Malformed payload (no recipient) — fail it so it backs off / abandons rather than spinning.
                await FailAsync(store, message, "payload missing 'to'", ct);
                return;
            }

            var result = await sender.SendAsync(message, ct);
            if (result.Success && result.ProviderMessageId is { Length: > 0 })
            {
                await store.MarkSentAsync(message.OutboxId, result.ProviderMessageId, DateTime.UtcNow, ct);
                logger.LogInformation(
                    "Outbox {OutboxId} (intent={Intent}) sent → {ProviderMessageId}",
                    message.OutboxId, message.MessageIntent, result.ProviderMessageId);
            }
            else
            {
                await FailAsync(store, message, result.Error ?? "unknown send failure", ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown mid-send: leave the row in 'processing'. A reaper or the next start can requeue stuck
            // 'processing' rows; we intentionally do NOT mark it failed on a clean shutdown.
            throw;
        }
        catch (Exception ex)
        {
            // A send exception MUST NOT crash the loop — record it as a failure and move on.
            logger.LogWarning(ex, "Outbox {OutboxId} send threw; recording failure.", message.OutboxId);
            await SafeFailAsync(store, message, ex.Message, ct);
        }
    }

    private async Task FailAsync(IOutboxDrainStore store, OutboundMessage message, string error, CancellationToken ct)
    {
        var nextRetryAt = ComputeNextRetry(message.AttemptCount);
        await store.MarkFailedAsync(message.OutboxId, Truncate(error), nextRetryAt, DateTime.UtcNow, ct);

        // attempt_count on the message is the PRE-increment value; the store increments to attemptCount+1.
        var willAbandon = message.AttemptCount + 1 >= message.MaxAttempts;
        if (willAbandon)
            logger.LogWarning(
                "Outbox {OutboxId} abandoned after {Attempts} attempt(s): {Error}",
                message.OutboxId, message.AttemptCount + 1, error);
        else
            logger.LogWarning(
                "Outbox {OutboxId} send failed (attempt {Attempts}/{Max}); retry at {NextRetry:o}: {Error}",
                message.OutboxId, message.AttemptCount + 1, message.MaxAttempts, nextRetryAt, error);
    }

    /// <summary>Best-effort failure write — if even THIS throws (DB down), swallow so the loop survives.</summary>
    private async Task SafeFailAsync(IOutboxDrainStore store, OutboundMessage message, string error, CancellationToken ct)
    {
        try
        {
            await FailAsync(store, message, error, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Outbox {OutboxId}: failed to even record the failure (row stays 'processing').", message.OutboxId);
        }
    }

    /// <summary>
    /// Exponential backoff: <c>base * 2^(attemptCount)</c> seconds from now, capped at the configured max.
    /// <paramref name="attemptCount"/> is the pre-increment count (0 on the first failure → base delay).
    /// </summary>
    private DateTime ComputeNextRetry(int attemptCount)
    {
        var baseSeconds = Math.Max(1, _options.BackoffBaseSeconds);
        var capSeconds = Math.Max(baseSeconds, _options.BackoffMaxSeconds);

        // 2^attemptCount with an exponent clamp so we never overflow on a misconfigured max_attempts.
        var exponent = Math.Min(attemptCount, 16);
        var delaySeconds = Math.Min((long)baseSeconds * (1L << exponent), capSeconds);
        return DateTime.UtcNow.AddSeconds(delaySeconds);
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
