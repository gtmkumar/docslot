namespace mediq.Application.Options;

/// <summary>
/// Configures the phase-4 RETENTION-PRUNER sweep worker (<c>RetentionPruneWorker</c>). On a slow cadence it
/// physically deletes AGED, terminal <c>status='success'</c> rows from the two append-only platform_api
/// operational queue/delivery tables (<c>integration_event_outbox</c>, <c>webhook_deliveries</c>) to close the
/// unbounded-growth ops hazard. It is a SWEEP, not a drain: no claim/lease/SKIP LOCKED — just a batched DELETE
/// loop per table per tick, bounding lock time on a large backlog.
/// <para>
/// DEFAULT-OFF (<see cref="PrunerEnabled"/> = false) and force-OFF in the integration suite (TestHostConfig):
/// enabling physical deletion is a deliberate ops act. Only <c>success</c> rows past the retention window are
/// removed — <c>pending</c>/<c>processing</c>/<c>failed</c> are never touched (<c>failed</c> is RETRYABLE) and
/// <c>abandoned</c> dead-letters are KEPT as forensic evidence.
/// </para>
/// </summary>
public sealed class RetentionPruneOptions
{
    public const string SectionName = "Retention";

    /// <summary>Run the background prune loop in this instance. DEFAULT FALSE — physical deletion is opt-in;
    /// the worker is only registered (Program) when this is true, and the integration suite forces it false.</summary>
    public bool PrunerEnabled { get; set; }

    /// <summary>Hours between prune ticks (default 24 — a slow cadence; this is housekeeping, not a hot path).</summary>
    public int PruneIntervalHours { get; set; } = 24;

    /// <summary>Retention window (days) for terminal <c>success</c> integration-event outbox rows. Older success
    /// rows are pruned by <c>published_at</c>; non-success rows are never deleted.</summary>
    public int IntegrationEventSuccessRetentionDays { get; set; } = 30;

    /// <summary>Retention window (days) for terminal <c>success</c> webhook deliveries. Older success rows are
    /// pruned by <c>delivered_at</c>; non-success rows are never deleted.</summary>
    public int WebhookDeliverySuccessRetentionDays { get; set; } = 30;

    /// <summary>Rows deleted per DELETE statement. A subselect+LIMIT keeps each statement's lock short on a
    /// large backlog (the prune is batched/looped rather than one unbounded DELETE).</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>Max DELETE batches executed per table per tick — an upper bound on the work (and lock churn)
    /// any single tick performs; the next tick continues draining a deeper backlog.</summary>
    public int MaxBatchesPerTick { get; set; } = 50;
}
