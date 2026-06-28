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
/// The worker is cross-tenant by design (it drains every tenant's outbox) and runs with no per-request
/// tenant context, so it CANNOT use a plain app-role query: <c>docslot.outbox_messages</c> is RLS-protected
/// (file 05). Every operation therefore goes through a SECURITY DEFINER function
/// (<c>claim_due_outbox</c>/<c>mark_outbox_sent</c>/<c>mark_outbox_failed</c>/<c>requeue_stranded_outbox</c>)
/// that legitimately bypasses RLS while still stamping/returning <c>tenant_id</c>. Enqueue + the conversation
/// read run with a tenant context and satisfy the policy directly.
/// </para>
/// </summary>
public sealed class OutboxDrainStore(PlatformDbContext db) : IOutboxDrainStore
{
    public async Task<IReadOnlyList<OutboundMessage>> ClaimDueAsync(int batchSize, DateTime nowUtc, CancellationToken ct)
    {
        // Claim via the SECURITY DEFINER fn: the worker is cross-tenant and has no app.tenant_id, so a plain
        // app-role query would match zero rows under outbox RLS (file 05). The fn locks due rows
        // (FOR UPDATE SKIP LOCKED), flips them to 'processing', and returns them — safe across scale-out.
        var rows = await db.Database.SqlQueryRaw<ClaimRow>(
                """
                SELECT outbox_id AS "OutboxId", tenant_id AS "TenantId", patient_id AS "PatientId",
                       message_intent AS "MessageIntent", to_phone AS "ToPhone", body AS "Text",
                       correlation_id AS "CorrelationId", attempt_count AS "AttemptCount", max_attempts AS "MaxAttempts"
                FROM docslot.claim_due_outbox(@p1)
                """,
                new NpgsqlParameter("@p1", batchSize))
            .ToListAsync(ct);

        return rows
            .Select(r => new OutboundMessage(
                r.OutboxId, r.TenantId, r.PatientId, r.MessageIntent,
                r.ToPhone ?? string.Empty, r.Text ?? string.Empty,
                r.CorrelationId, r.AttemptCount, r.MaxAttempts))
            .ToList();
    }

    public Task MarkSentAsync(Guid outboxId, string providerMessageId, DateTime nowUtc, CancellationToken ct) =>
        // Definer fn marks 'sent' and scrubs the consent-OTP body post-delivery (DPDP).
        db.Database.ExecuteSqlRawAsync(
            "SELECT docslot.mark_outbox_sent(@p0, @p1, @p2)",
            new NpgsqlParameter("@p0", outboxId),
            new NpgsqlParameter("@p1", providerMessageId),
            new NpgsqlParameter("@p2", nowUtc));

    public Task MarkFailedAsync(Guid outboxId, string error, DateTime nextRetryAtUtc, DateTime nowUtc, CancellationToken ct) =>
        // Definer fn: attempt_count++ then terminal 'abandoned' at max_attempts, else 'pending' + backoff.
        db.Database.ExecuteSqlRawAsync(
            "SELECT docslot.mark_outbox_failed(@p0, @p1, @p2)",
            new NpgsqlParameter("@p0", outboxId),
            new NpgsqlParameter("@p1", error),
            new NpgsqlParameter("@p2", nextRetryAtUtc));

    public async Task<int> RequeueStrandedAsync(TimeSpan olderThan, CancellationToken ct)
    {
        // SECURITY DEFINER fn requeues 'processing' rows older than the threshold back to 'pending'.
        var rows = await db.Database.SqlQueryRaw<IntResult>(
                "SELECT docslot.requeue_stranded_outbox(make_interval(secs => @p0)) AS \"Value\"",
                new NpgsqlParameter("@p0", (int)olderThan.TotalSeconds))
            .ToListAsync(ct);
        return rows.FirstOrDefault()?.Value ?? 0;
    }

    private sealed record IntResult(int Value);

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
