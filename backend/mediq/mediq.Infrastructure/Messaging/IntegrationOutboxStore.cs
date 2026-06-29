using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Messaging;

/// <summary>
/// The CAPTURE half of the integration-event outbox. <see cref="RecordAsync"/> inserts a 'pending'
/// <c>platform_api.integration_event_outbox</c> row on the AMBIENT connection/transaction (raw SQL via the
/// shared <see cref="PlatformDbContext"/>, exactly like <c>WebhookDeliveryStore.EnqueueAsync</c> writes
/// <c>webhook_deliveries</c>) — so the row is written ATOMICALLY with the business write inside the command's
/// UnitOfWork transaction. If the command rolls back, the outbox row never persists.
/// <para>
/// <c>ON CONFLICT (event_id) DO NOTHING</c> makes capture idempotent: the same producer-assigned EventId can
/// be recorded twice (re-publish, retry) and lands at most one row.
/// </para>
/// </summary>
public sealed class IntegrationOutboxStore(PlatformDbContext db) : IIntegrationOutboxStore
{
    public Task RecordAsync(IntegrationEvent evt, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform_api.integration_event_outbox
                (event_id, event_type, tenant_id, payload, correlation_id, occurred_at, status, attempt_count, created_at)
            VALUES (@p0, @p1, @p2, CAST(@p3 AS jsonb), @p4, @p5, 'pending', 0, @p6)
            ON CONFLICT (event_id) DO NOTHING
            """,
            [
                new NpgsqlParameter("@p0", evt.EventId),
                new NpgsqlParameter("@p1", evt.EventType),
                new NpgsqlParameter("@p2", (object?)evt.TenantId ?? DBNull.Value),
                new NpgsqlParameter("@p3", evt.PayloadJson),
                new NpgsqlParameter("@p4", (object?)evt.CorrelationId ?? DBNull.Value),
                new NpgsqlParameter("@p5", evt.OccurredAtUtc),
                new NpgsqlParameter("@p6", evt.OccurredAtUtc),
            ],
            ct);
}
