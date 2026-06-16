using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Docslot.WhatsApp;

/// <summary>
/// Drain-side persistence over <c>docslot.outbox_messages</c>. The claim step is the concurrency-critical
/// one: a CTE selects due 'pending' rows <c>FOR UPDATE SKIP LOCKED</c> (so a second worker/instance simply
/// skips rows already locked by the first) and the outer <c>UPDATE ... RETURNING</c> flips them to
/// 'processing' in the SAME statement — no row can be claimed twice, so no message is double-sent.
/// <para>
/// The worker is cross-tenant by design (it drains every tenant's outbox), so this store does NOT open a
/// per-tenant RLS scope. <c>docslot.outbox_messages</c> is an operational queue (no PHI in the row itself;
/// the payload text is the only sensitive part) and the canonical schema does not put it behind RLS — the
/// row carries <c>tenant_id</c> for attribution, which we always read back and surface on the projected
/// <see cref="OutboundMessage"/>. Each claimed message still carries its <c>tenant_id</c> for downstream use.
/// </para>
/// </summary>
public sealed class OutboxDrainStore(PlatformDbContext db) : IOutboxDrainStore
{
    public async Task<IReadOnlyList<OutboundMessage>> ClaimDueAsync(int batchSize, DateTime nowUtc, CancellationToken ct)
    {
        // CTE: lock the due rows (skip ones another worker holds), then update them to 'processing' and
        // return the claimed rows. payload is jsonb {to, text, ...} → extract the fields the sender needs.
        var rows = await db.Database.SqlQueryRaw<ClaimRow>(
                """
                WITH due AS (
                    SELECT outbox_id
                    FROM docslot.outbox_messages
                    WHERE status = 'pending'
                      AND (next_retry_at IS NULL OR next_retry_at <= @p0)
                    ORDER BY created_at
                    FOR UPDATE SKIP LOCKED
                    LIMIT @p1
                )
                UPDATE docslot.outbox_messages o
                SET status = 'processing'
                FROM due
                WHERE o.outbox_id = due.outbox_id
                RETURNING
                    o.outbox_id      AS "OutboxId",
                    o.tenant_id      AS "TenantId",
                    o.patient_id     AS "PatientId",
                    o.message_intent AS "MessageIntent",
                    o.payload->>'to'   AS "ToPhone",
                    o.payload->>'text' AS "Text",
                    o.correlation_id AS "CorrelationId",
                    o.attempt_count  AS "AttemptCount",
                    o.max_attempts   AS "MaxAttempts"
                """,
                new NpgsqlParameter("@p0", nowUtc),
                new NpgsqlParameter("@p1", batchSize))
            .ToListAsync(ct);

        return rows
            .Select(r => new OutboundMessage(
                r.OutboxId, r.TenantId, r.PatientId, r.MessageIntent,
                r.ToPhone ?? string.Empty, r.Text ?? string.Empty,
                r.CorrelationId, r.AttemptCount, r.MaxAttempts))
            .ToList();
    }

    public async Task MarkSentAsync(Guid outboxId, string providerMessageId, DateTime nowUtc, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE docslot.outbox_messages
            SET status = 'sent',
                sent_at = @p1,
                whatsapp_message_id = @p2,
                last_error = NULL
            WHERE outbox_id = @p0
            """,
            new[]
            {
                new NpgsqlParameter("@p0", outboxId),
                new NpgsqlParameter("@p1", nowUtc),
                new NpgsqlParameter("@p2", providerMessageId),
            }, ct);
    }

    public async Task MarkFailedAsync(Guid outboxId, string error, DateTime nextRetryAtUtc, DateTime nowUtc, CancellationToken ct)
    {
        // attempt_count was already incremented? No — increment HERE, atomically, and decide terminal-vs-retry
        // from the post-increment value so two failures can't race past max_attempts. When the incremented
        // count reaches max_attempts → 'abandoned' (dead-letter); else back to 'pending' with next_retry_at.
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE docslot.outbox_messages
            SET attempt_count = attempt_count + 1,
                last_error = @p1,
                status = CASE WHEN attempt_count + 1 >= max_attempts THEN 'abandoned' ELSE 'pending' END,
                next_retry_at = CASE WHEN attempt_count + 1 >= max_attempts THEN next_retry_at ELSE @p2 END
            WHERE outbox_id = @p0
            """,
            new[]
            {
                new NpgsqlParameter("@p0", outboxId),
                new NpgsqlParameter("@p1", error),
                new NpgsqlParameter("@p2", nextRetryAtUtc),
            }, ct);
    }

    private sealed record ClaimRow(
        Guid OutboxId,
        Guid TenantId,
        Guid? PatientId,
        string MessageIntent,
        string? ToPhone,
        string? Text,
        string? CorrelationId,
        int AttemptCount,
        int MaxAttempts);
}
