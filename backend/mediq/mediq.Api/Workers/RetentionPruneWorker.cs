using mediq.Application.Abstractions;
using mediq.Application.Options;
using Microsoft.Extensions.Options;

namespace mediq.Api.Workers;

/// <summary>
/// Phase-4 RETENTION-PRUNER sweep. On a slow tick (default every 24 h) it physically deletes AGED, terminal
/// <c>status='success'</c> rows from the two append-only platform_api operational tables
/// (<c>integration_event_outbox</c>, <c>webhook_deliveries</c>), closing the unbounded-growth ops hazard.
/// <para>
/// It is a SWEEP, not a drain: no claim/lease/SKIP LOCKED — each tick runs a batched DELETE loop per table
/// (bounding lock time on a large backlog) and logs the row counts it removed. Only <c>success</c> rows past the
/// retention window are deleted — <c>pending</c>/<c>processing</c>/<c>failed</c> are never touched (<c>failed</c>
/// is RETRYABLE) and <c>abandoned</c> dead-letters are kept as forensic evidence.
/// </para>
/// <para>
/// Singleton hosted service → opens a DI scope per tick for the scoped <see cref="IRetentionPruneStore"/> (+ its
/// DbContext). A tick failure is logged and never crashes the host. DISABLED BY DEFAULT — registered only when
/// Retention:PrunerEnabled=true (and force-off in the integration suite via TestHostConfig).
/// </para>
/// </summary>
public sealed class RetentionPruneWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<RetentionPruneOptions> options,
    ILogger<RetentionPruneWorker> logger) : BackgroundService
{
    private readonly RetentionPruneOptions _opts = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, _opts.PruneIntervalHours));
        logger.LogInformation(
            "RetentionPruneWorker started (every {Hours}h; outbox>{OutboxDays}d, webhooks>{WebhookDays}d; batch={Batch}, maxBatches={Max}).",
            interval.TotalHours, _opts.IntegrationEventSuccessRetentionDays, _opts.WebhookDeliverySuccessRetentionDays,
            _opts.BatchSize, _opts.MaxBatchesPerTick);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "RetentionPruneWorker tick failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRetentionPruneStore>();
        var now = DateTime.UtcNow;

        var outboxPruned = await store.PruneSuccessfulIntegrationEventsAsync(
            _opts.IntegrationEventSuccessRetentionDays, _opts.BatchSize, _opts.MaxBatchesPerTick, now, ct);
        if (outboxPruned > 0)
            logger.LogInformation(
                "RetentionPrune: deleted {Count} success integration-event outbox rows older than {Days}d.",
                outboxPruned, _opts.IntegrationEventSuccessRetentionDays);

        var webhooksPruned = await store.PruneSuccessfulWebhookDeliveriesAsync(
            _opts.WebhookDeliverySuccessRetentionDays, _opts.BatchSize, _opts.MaxBatchesPerTick, now, ct);
        if (webhooksPruned > 0)
            logger.LogInformation(
                "RetentionPrune: deleted {Count} success webhook deliveries older than {Days}d.",
                webhooksPruned, _opts.WebhookDeliverySuccessRetentionDays);
    }
}
