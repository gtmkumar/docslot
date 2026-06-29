using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.PlatformApi;

/// <summary>
/// Drain-side persistence over <c>platform_api.webhook_deliveries</c>. The webhook tables are NOT RLS-protected
/// (platform-API PaaS layer; the app role holds direct grants), so — unlike the WhatsApp outbox — this needs no
/// SECURITY DEFINER function: the claim is a plain app-role <c>UPDATE … FROM (… FOR UPDATE SKIP LOCKED)</c> that
/// flips due rows to 'processing' in one statement, so a second worker/instance can never claim the same row.
/// A visibility lease written into <c>next_retry_at</c> at claim time lets a later tick re-claim a delivery left
/// in 'processing' by a crashed worker (webhooks are at-least-once; subscribers dedupe on <c>event_id</c>).
/// </summary>
public sealed class WebhookDeliveryDrainStore(PlatformDbContext db) : IWebhookDeliveryDrainStore
{
    public async Task<IReadOnlyList<ClaimedWebhookDelivery>> ClaimDueAsync(int batchSize, int leaseSeconds, DateTime nowUtc, CancellationToken ct)
    {
        // Claim due deliveries whose subscription is still active. "Due" = pending, or failed/processing whose
        // next_retry_at (backoff schedule, or the processing lease) has elapsed. The lock is taken on the
        // delivery rows only (FOR UPDATE OF d), SKIP LOCKED so concurrent workers skip each other's claims.
        var rows = await db.Database.SqlQueryRaw<ClaimRow>(
                """
                WITH due AS (
                    SELECT d.delivery_id
                    FROM platform_api.webhook_deliveries d
                    JOIN platform_api.webhook_subscriptions s ON s.webhook_id = d.webhook_id
                    WHERE d.status IN ('pending', 'failed', 'processing')
                      AND (d.next_retry_at IS NULL OR d.next_retry_at <= @p_now)
                      AND s.is_active AND s.auto_disabled_at IS NULL
                    ORDER BY d.next_retry_at NULLS FIRST, d.created_at
                    FOR UPDATE OF d SKIP LOCKED
                    LIMIT @p_batch
                )
                UPDATE platform_api.webhook_deliveries d
                SET status = 'processing', next_retry_at = @p_now + make_interval(secs => @p_lease)
                FROM due, platform_api.webhook_subscriptions s
                WHERE d.delivery_id = due.delivery_id AND s.webhook_id = d.webhook_id
                RETURNING d.delivery_id AS "DeliveryId", d.webhook_id AS "WebhookId", d.event_type AS "EventType",
                          d.event_id AS "EventId", d.payload::text AS "PayloadJson", d.attempt_count::int AS "AttemptCount",
                          s.url AS "Url", s.secret_hash AS "SecretHash", s.timeout_seconds::int AS "TimeoutSeconds",
                          s.max_retries::int AS "MaxRetries"
                """,
                new NpgsqlParameter("@p_now", nowUtc),
                new NpgsqlParameter("@p_batch", batchSize),
                new NpgsqlParameter("@p_lease", leaseSeconds))
            .ToListAsync(ct);

        return rows
            .Select(r => new ClaimedWebhookDelivery(
                r.DeliveryId, r.WebhookId, r.EventType, r.EventId, r.PayloadJson ?? "{}",
                r.AttemptCount, r.Url, r.SecretHash, r.TimeoutSeconds, r.MaxRetries))
            .ToList();
    }

    public Task MarkDeliveredAsync(Guid deliveryId, string signature, int statusCode, int responseMs, DateTime nowUtc, CancellationToken ct) =>
        // The subscription-health reset is gated (CTE RETURNING) on the delivery UPDATE having matched, so a
        // single-winner LOSER (a stale call on a row no longer 'processing') can't perturb the subscription's
        // consecutive_failures / last_success_at under a lease collision (auditor finding).
        db.Database.ExecuteSqlRawAsync(
            """
            WITH won AS (
                UPDATE platform_api.webhook_deliveries
                SET status = 'success', signature = @p1, response_status_code = @p2, response_time_ms = @p3,
                    attempt_count = attempt_count + 1, delivered_at = @p4, next_retry_at = NULL, error_message = NULL
                WHERE delivery_id = @p0 AND status = 'processing'
                RETURNING webhook_id
            )
            UPDATE platform_api.webhook_subscriptions s
            SET last_success_at = @p4, consecutive_failures = 0
            FROM won WHERE s.webhook_id = won.webhook_id;
            """,
            new NpgsqlParameter("@p0", deliveryId), new NpgsqlParameter("@p1", signature),
            new NpgsqlParameter("@p2", statusCode), new NpgsqlParameter("@p3", responseMs),
            new NpgsqlParameter("@p4", nowUtc));

    public Task MarkFailedAsync(Guid deliveryId, string signature, int? statusCode, string error, int maxRetries,
        int autoDisableThreshold, DateTime nextRetryUtc, DateTime nowUtc, CancellationToken ct) =>
        // attempt++ then 'abandoned' at maxRetries (next_retry_at cleared), else 'failed' + the backoff schedule.
        // The subscription health bump (consecutive_failures / auto-disable) is gated (CTE RETURNING) on the
        // delivery UPDATE having matched, so a single-winner LOSER can't double-bump a healthy subscription
        // toward auto-disable under a lease collision (auditor finding).
        db.Database.ExecuteSqlRawAsync(
            """
            WITH won AS (
                UPDATE platform_api.webhook_deliveries
                SET attempt_count = attempt_count + 1, signature = @p1, response_status_code = @p2, error_message = @p3,
                    status = CASE WHEN attempt_count + 1 > @p4 THEN 'abandoned' ELSE 'failed' END,
                    next_retry_at = CASE WHEN attempt_count + 1 > @p4 THEN NULL ELSE @p5 END
                WHERE delivery_id = @p0 AND status = 'processing'
                RETURNING webhook_id
            )
            UPDATE platform_api.webhook_subscriptions s
            SET consecutive_failures = s.consecutive_failures + 1, last_failure_at = @p6,
                auto_disabled_at = CASE WHEN s.consecutive_failures + 1 >= @p7 AND s.auto_disabled_at IS NULL THEN @p6 ELSE s.auto_disabled_at END
            FROM won WHERE s.webhook_id = won.webhook_id;
            """,
            new NpgsqlParameter("@p0", deliveryId), new NpgsqlParameter("@p1", signature),
            new NpgsqlParameter("@p2", (object?)statusCode ?? DBNull.Value), new NpgsqlParameter("@p3", error),
            new NpgsqlParameter("@p4", maxRetries), new NpgsqlParameter("@p5", nextRetryUtc),
            new NpgsqlParameter("@p6", nowUtc), new NpgsqlParameter("@p7", autoDisableThreshold));

    private sealed record ClaimRow(
        Guid DeliveryId, Guid WebhookId, string EventType, Guid EventId, string? PayloadJson,
        int AttemptCount, string Url, string SecretHash, int TimeoutSeconds, int MaxRetries);
}
