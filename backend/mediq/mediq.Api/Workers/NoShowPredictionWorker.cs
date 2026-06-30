using mediq.Application.Abstractions;
using mediq.Application.Options;
using Microsoft.Extensions.Options;

namespace mediq.Api.Workers;

/// <summary>
/// Background driver for the PROACTIVE NO-SHOW PREDICTION BACKFILL (slice 16). On a configurable interval it
/// asks <see cref="INoShowBackfillRunner"/> to score a batch of upcoming, not-yet-predicted bookings via the AI
/// sibling service, marking each scored booking so it is never re-predicted (the idempotency marker). DEFAULT-OFF
/// (registered in Program only when <c>NoShowBackfill:Enabled</c> is true) — proactive scoring is opt-in.
/// <para>
/// Resilience: a whole-tick failure (e.g. transient DB outage) is caught and logged; the loop continues on the
/// next tick and never crashes the host. Per-booking failures are isolated inside the runner. Scoping: this is a
/// singleton hosted service, so it opens a DI SCOPE per tick to resolve the scoped <see cref="INoShowBackfillRunner"/>
/// (and its scoped store / DbContext). NO PHI or service token is ever logged — counts/status only.
/// </para>
/// </summary>
public sealed class NoShowPredictionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<NoShowBackfillOptions> options,
    ILogger<NoShowPredictionWorker> logger) : BackgroundService
{
    private readonly NoShowBackfillOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds));
        logger.LogInformation(
            "NoShowPredictionWorker started (interval={IntervalSeconds}s, batch={BatchSize}, window={WindowHours}h).",
            _options.IntervalSeconds, _options.BatchSize, _options.WindowHours);

        using var timer = new PeriodicTimer(interval);

        // Run once immediately, then on every tick. PeriodicTimer.WaitForNextTickAsync returns false only on
        // cancellation, which cleanly exits the loop on shutdown.
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;   // shutdown
            }
            catch (Exception ex)
            {
                // A whole-tick failure (e.g. transient DB outage on the scan) must not kill the worker.
                logger.LogError(ex, "NoShowPredictionWorker tick failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        logger.LogInformation("NoShowPredictionWorker stopping.");
    }

    /// <summary>One backfill pass in its own DI scope (the runner + store + DbContext are scoped).</summary>
    private async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<INoShowBackfillRunner>();
        await runner.RunOnceAsync(ct);
    }
}
