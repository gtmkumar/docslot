namespace mediq.Application.Abstractions;

/// <summary>
/// Retention-prune persistence for the two append-only platform_api operational queue/delivery tables
/// (<c>integration_event_outbox</c>, <c>webhook_deliveries</c>). Driven by the phase-4 SWEEP worker
/// (<c>RetentionPruneWorker</c>), it physically deletes AGED, terminal <c>status='success'</c> rows to close the
/// unbounded-growth ops hazard.
/// <para>
/// SAFETY INVARIANT: only <c>status='success'</c> rows are ever deleted. <c>failed</c> is RETRYABLE (the drain
/// re-claims it on its <c>next_retry_at</c> backoff), and <c>pending</c>/<c>processing</c> are in-flight, so
/// deleting any of those would be data loss; <c>abandoned</c> dead-letters are KEPT as forensic evidence. Each
/// method deletes in batches (subselect + LIMIT, looped) so each statement's lock stays short on a large backlog.
/// </para>
/// </summary>
public interface IRetentionPruneStore
{
    /// <summary>
    /// Physically deletes ONLY <c>status='success'</c> integration-event outbox rows older than
    /// <paramref name="retentionDays"/> (keyed on <c>published_at</c>), in batches of <paramref name="batchSize"/>
    /// up to <paramref name="maxBatches"/> per call; returns the total rows deleted. Never touches
    /// <c>pending</c>/<c>processing</c>/<c>failed</c>/<c>abandoned</c> rows.
    /// </summary>
    Task<int> PruneSuccessfulIntegrationEventsAsync(int retentionDays, int batchSize, int maxBatches, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Physically deletes ONLY <c>status='success'</c> webhook deliveries older than
    /// <paramref name="retentionDays"/> (keyed on <c>delivered_at</c>), in batches of <paramref name="batchSize"/>
    /// up to <paramref name="maxBatches"/> per call; returns the total rows deleted. Never touches
    /// <c>pending</c>/<c>processing</c>/<c>failed</c>/<c>abandoned</c> rows.
    /// </summary>
    Task<int> PruneSuccessfulWebhookDeliveriesAsync(int retentionDays, int batchSize, int maxBatches, DateTime nowUtc, CancellationToken ct);
}
