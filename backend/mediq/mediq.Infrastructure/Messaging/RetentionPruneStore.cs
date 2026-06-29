using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Messaging;

/// <summary>
/// Retention-prune persistence over the two append-only platform_api operational tables
/// (<c>integration_event_outbox</c>, <c>webhook_deliveries</c>). Like the drain stores, the platform_api tables
/// are NOT RLS-protected (PaaS layer; the app role holds direct grants), so this needs NO SECURITY DEFINER — it
/// runs as plain app-role SQL under the narrow DELETE grant added in 10_roles_grants.sql.
/// <para>
/// SAFETY INVARIANT (the whole point of this store): each DELETE matches ONLY <c>status='success'</c> rows past
/// the retention window. <c>'failed'</c> is RETRYABLE — the drain re-claims it on its <c>next_retry_at</c>
/// backoff — and <c>'pending'</c>/<c>'processing'</c> are in-flight, so deleting any of them would be data loss
/// (the exact class of bug a prior slice fixed). <c>'abandoned'</c> dead-letters are KEPT as forensic evidence.
/// The <c>status='success'</c> predicate plus the <c>&lt;completion_ts&gt; IS NOT NULL</c> belt-and-suspenders
/// guard make the DELETE incapable of matching a non-terminal row. The success-only partial retention indexes
/// (<c>idx_*_retention</c>) back the scan.
/// </para>
/// <para>
/// Each method is a BATCHED delete loop (subselect + LIMIT) so each statement's lock stays short on a large
/// backlog; it stops early once a batch comes back short (the backlog is drained).
/// </para>
/// </summary>
public sealed class RetentionPruneStore(PlatformDbContext db) : IRetentionPruneStore
{
    public async Task<int> PruneSuccessfulIntegrationEventsAsync(
        int retentionDays, int batchSize, int maxBatches, DateTime nowUtc, CancellationToken ct)
    {
        var cutoff = nowUtc.AddDays(-retentionDays);
        var total = 0;
        for (var i = 0; i < maxBatches; i++)
        {
            // Success rows ALWAYS carry published_at (set atomically with status='success' by MarkPublishedAsync);
            // the IS NOT NULL guard is belt-and-suspenders so a (impossible) null-stamped success row never matches.
            var deleted = await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM platform_api.integration_event_outbox
                WHERE outbox_id IN (
                    SELECT outbox_id FROM platform_api.integration_event_outbox
                    WHERE status = 'success' AND published_at IS NOT NULL AND published_at < @p0
                    ORDER BY published_at
                    LIMIT @p1)
                """,
                [new NpgsqlParameter("@p0", cutoff), new NpgsqlParameter("@p1", batchSize)], ct);
            total += deleted;
            if (deleted < batchSize) break;   // backlog drained
        }
        return total;
    }

    public async Task<int> PruneSuccessfulWebhookDeliveriesAsync(
        int retentionDays, int batchSize, int maxBatches, DateTime nowUtc, CancellationToken ct)
    {
        var cutoff = nowUtc.AddDays(-retentionDays);
        var total = 0;
        for (var i = 0; i < maxBatches; i++)
        {
            // Success deliveries ALWAYS carry delivered_at (set atomically with status='success' by
            // MarkDeliveredAsync); the IS NOT NULL guard keeps a null-stamped row from ever matching.
            var deleted = await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM platform_api.webhook_deliveries
                WHERE delivery_id IN (
                    SELECT delivery_id FROM platform_api.webhook_deliveries
                    WHERE status = 'success' AND delivered_at IS NOT NULL AND delivered_at < @p0
                    ORDER BY delivered_at
                    LIMIT @p1)
                """,
                [new NpgsqlParameter("@p0", cutoff), new NpgsqlParameter("@p1", batchSize)], ct);
            total += deleted;
            if (deleted < batchSize) break;   // backlog drained
        }
        return total;
    }
}
