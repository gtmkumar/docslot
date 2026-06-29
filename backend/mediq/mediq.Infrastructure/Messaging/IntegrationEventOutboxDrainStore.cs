using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Messaging;

/// <summary>
/// Drain-side persistence over <c>platform_api.integration_event_outbox</c>. The platform_api tables are NOT
/// RLS-protected (PaaS layer; the app role holds direct grants), so — like <c>WebhookDeliveryDrainStore</c> —
/// this needs no SECURITY DEFINER function: the claim is a plain app-role <c>UPDATE … FROM (… FOR UPDATE SKIP
/// LOCKED)</c> that flips due rows to 'processing' in one statement, so a second worker can never claim the same
/// row. A visibility lease written into <c>next_retry_at</c> at claim time lets a later tick re-claim a row left
/// 'processing' by a crashed worker (at-least-once publish; consumers dedupe on the <c>x-event-id</c> header).
/// <para>
/// Unlike the webhook drain there is NO subscription join — events fan to the broker, not per-subscriber, so it
/// is one row per event, claimed once, published once.
/// </para>
/// </summary>
public sealed class IntegrationEventOutboxDrainStore(PlatformDbContext db) : IIntegrationEventOutboxDrainStore
{
    public async Task<IReadOnlyList<ClaimedIntegrationEvent>> ClaimDueAsync(int batchSize, int leaseSeconds, DateTime nowUtc, CancellationToken ct)
    {
        // "Due" = pending, or failed/processing whose next_retry_at (backoff schedule, or the processing lease)
        // has elapsed. The lock is taken on the outbox rows only, SKIP LOCKED so concurrent workers skip each
        // other's claims. The claim itself sets a fresh lease so a crashed worker's row reappears after leaseSeconds.
        var rows = await db.Database.SqlQueryRaw<ClaimRow>(
                """
                WITH due AS (
                    SELECT o.outbox_id
                    FROM platform_api.integration_event_outbox o
                    WHERE o.status IN ('pending', 'failed', 'processing')
                      AND (o.next_retry_at IS NULL OR o.next_retry_at <= @p_now)
                    ORDER BY o.next_retry_at NULLS FIRST, o.created_at
                    FOR UPDATE SKIP LOCKED
                    LIMIT @p_batch
                )
                UPDATE platform_api.integration_event_outbox o
                SET status = 'processing', next_retry_at = @p_now + make_interval(secs => @p_lease)
                FROM due
                WHERE o.outbox_id = due.outbox_id
                RETURNING o.outbox_id AS "OutboxId", o.event_id AS "EventId", o.event_type AS "EventType",
                          o.tenant_id AS "TenantId", o.payload::text AS "PayloadJson", o.correlation_id AS "CorrelationId",
                          o.occurred_at AS "OccurredAt", o.attempt_count::int AS "AttemptCount"
                """,
                new NpgsqlParameter("@p_now", nowUtc),
                new NpgsqlParameter("@p_batch", batchSize),
                new NpgsqlParameter("@p_lease", leaseSeconds))
            .ToListAsync(ct);

        return rows
            .Select(r => new ClaimedIntegrationEvent(
                r.OutboxId, r.EventId, r.EventType, r.TenantId, r.PayloadJson ?? "{}",
                r.CorrelationId, r.OccurredAt, r.AttemptCount))
            .ToList();
    }

    public Task MarkPublishedAsync(Guid outboxId, DateTime nowUtc, CancellationToken ct) =>
        // Single-winner: the WHERE status='processing' guard means a stale call on a row that a later tick has
        // already re-claimed (or marked) is a no-op — it can't overwrite a fresher attempt.
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE platform_api.integration_event_outbox
            SET status = 'success', attempt_count = attempt_count + 1, published_at = @p1,
                next_retry_at = NULL, last_error = NULL
            WHERE outbox_id = @p0 AND status = 'processing'
            """,
            [new NpgsqlParameter("@p0", outboxId), new NpgsqlParameter("@p1", nowUtc)],
            ct);

    public Task MarkFailedAsync(Guid outboxId, string error, int maxRetries, DateTime nextRetryUtc, DateTime nowUtc, CancellationToken ct) =>
        // attempt++ then 'abandoned' at maxRetries (next_retry_at cleared), else 'failed' + the backoff schedule.
        // Same single-winner guard (status='processing') as MarkPublishedAsync.
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE platform_api.integration_event_outbox
            SET attempt_count = attempt_count + 1, last_error = @p1,
                status = CASE WHEN attempt_count + 1 > @p2 THEN 'abandoned' ELSE 'failed' END,
                next_retry_at = CASE WHEN attempt_count + 1 > @p2 THEN NULL ELSE @p3 END
            WHERE outbox_id = @p0 AND status = 'processing'
            """,
            [
                new NpgsqlParameter("@p0", outboxId),
                new NpgsqlParameter("@p1", error),
                new NpgsqlParameter("@p2", maxRetries),
                new NpgsqlParameter("@p3", nextRetryUtc),
            ],
            ct);

    private sealed record ClaimRow(
        Guid OutboxId, Guid EventId, string EventType, Guid? TenantId, string? PayloadJson,
        string? CorrelationId, DateTime OccurredAt, int AttemptCount);
}
