using mediq.Application.Abstractions;
using mediq.Domain.PlatformApi;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.PlatformApi;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.PlatformApi;

/// <summary>CRUD + fan-out lookups over <c>platform_api.webhook_subscriptions</c>.</summary>
public sealed class WebhookSubscriptionRepository(PlatformDbContext db) : IWebhookSubscriptionRepository
{
    public async Task<Guid> CreateAsync(CreateWebhookRequest req, string secretHash, DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform_api.webhook_subscriptions
                (webhook_id, client_id, tenant_id, name, url, secret_hash, event_types, max_retries,
                 timeout_seconds, is_active, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, true, @p9, @p9)
            """,
            new NpgsqlParameter("@p0", id),
            new NpgsqlParameter("@p1", req.ClientId),
            new NpgsqlParameter("@p2", (object?)req.TenantId ?? DBNull.Value),
            new NpgsqlParameter("@p3", req.Name),
            new NpgsqlParameter("@p4", req.Url),
            new NpgsqlParameter("@p5", secretHash),
            new NpgsqlParameter("@p6", req.EventTypes.ToArray()),
            new NpgsqlParameter("@p7", req.MaxRetries),
            new NpgsqlParameter("@p8", req.TimeoutSeconds),
            new NpgsqlParameter("@p9", nowUtc));
        return id;
    }

    public Task<WebhookSubscription?> GetByIdAsync(Guid webhookId, CancellationToken ct) =>
        db.WebhookSubscriptions.FirstOrDefaultAsync(w => w.WebhookId == webhookId, ct);

    public async Task<IReadOnlyList<WebhookSubscriptionDto>> ListByClientAsync(Guid clientId, CancellationToken ct)
    {
        var subs = await db.WebhookSubscriptions.AsNoTracking()
            .Where(w => w.ClientId == clientId)
            .OrderBy(w => w.Name)
            .ToListAsync(ct);

        // Last-7d success rate per subscription — ONE grouped query over the deliveries of this page's webhooks
        // (no per-subscription N+1). A webhook with no deliveries in the window is simply absent from the map,
        // so its rate resolves to null (divide-by-zero guarded).
        var ids = subs.Select(w => w.WebhookId).ToList();
        var since = DateTime.UtcNow.AddDays(-7);
        var stats = ids.Count == 0
            ? []
            : await db.WebhookDeliveries.AsNoTracking()
                .Where(d => ids.Contains(d.WebhookId) && d.CreatedAt >= since)
                .GroupBy(d => d.WebhookId)
                .Select(g => new { WebhookId = g.Key, Total = g.Count(), Delivered = g.Count(d => d.Status == "success") })
                .ToListAsync(ct);
        var rateByWebhook = stats.ToDictionary(
            s => s.WebhookId,
            s => s.Total == 0 ? (double?)null : (double)s.Delivered / s.Total);

        return subs.Select(w => new WebhookSubscriptionDto(
            w.WebhookId, w.ClientId, w.TenantId, w.Name, w.Url, w.EventTypes, w.MaxRetries,
            w.RetryBackoff, w.TimeoutSeconds, w.IsActive, w.ConsecutiveFailures,
            w.LastSuccessAt, w.LastFailureAt, w.AutoDisabledAt, w.CreatedAt,
            rateByWebhook.GetValueOrDefault(w.WebhookId))).ToList();
    }

    public Task UpdateAsync(Guid webhookId, UpdateWebhookRequest r, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE platform_api.webhook_subscriptions
            SET name = COALESCE(@p1, name),
                url = COALESCE(@p2, url),
                event_types = COALESCE(@p3, event_types),
                is_active = COALESCE(@p4, is_active)
            WHERE webhook_id = @p0
            """,
            new NpgsqlParameter("@p0", webhookId),
            new NpgsqlParameter("@p1", (object?)r.Name ?? DBNull.Value),
            new NpgsqlParameter("@p2", (object?)r.Url ?? DBNull.Value),
            new NpgsqlParameter("@p3", (object?)r.EventTypes?.ToArray() ?? DBNull.Value),
            new NpgsqlParameter("@p4", (object?)r.IsActive ?? DBNull.Value));

    public Task DeleteAsync(Guid webhookId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "DELETE FROM platform_api.webhook_subscriptions WHERE webhook_id = @p0",
            new NpgsqlParameter("@p0", webhookId));

    public async Task<IReadOnlyList<WebhookSubscription>> FindDeliverableAsync(string eventType, Guid? tenantId, CancellationToken ct)
    {
        // Active, not auto-disabled, subscribed to this event type, and (tenant-agnostic OR matching tenant).
        var query = db.WebhookSubscriptions.AsNoTracking()
            .Where(w => w.IsActive && w.AutoDisabledAt == null && w.EventTypes.Contains(eventType));
        query = tenantId is { } tid
            ? query.Where(w => w.TenantId == null || w.TenantId == tid)
            : query.Where(w => w.TenantId == null);
        return await query.ToListAsync(ct);
    }

    public Task RecordOutcomeAsync(Guid webhookId, bool success, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            success
                ? "UPDATE platform_api.webhook_subscriptions SET last_success_at = @p1, consecutive_failures = 0 WHERE webhook_id = @p0"
                : """
                  UPDATE platform_api.webhook_subscriptions
                  SET last_failure_at = @p1, consecutive_failures = consecutive_failures + 1,
                      auto_disabled_at = CASE WHEN consecutive_failures + 1 >= 20 THEN @p1 ELSE auto_disabled_at END
                  WHERE webhook_id = @p0
                  """,
            new NpgsqlParameter("@p0", webhookId), new NpgsqlParameter("@p1", nowUtc));
}

/// <summary>Reads the <c>platform_api.api_event_types</c> registry.</summary>
public sealed class EventTypeRepository(PlatformDbContext db) : IEventTypeRepository
{
    public async Task<IReadOnlyList<EventTypeDto>> ListAsync(CancellationToken ct) =>
        await db.ApiEventTypes.AsNoTracking()
            .OrderBy(e => e.EventType)
            .Select(e => new EventTypeDto(e.EventType, e.Resource, e.Action, e.Description, e.RequiresScope, e.IsActive))
            .ToListAsync(ct);

    public Task<bool> ExistsAndActiveAsync(string eventType, CancellationToken ct) =>
        db.ApiEventTypes.AsNoTracking().AnyAsync(e => e.EventType == eventType && e.IsActive, ct);
}

/// <summary>Outbox over <c>platform_api.webhook_deliveries</c>.</summary>
public sealed class WebhookDeliveryStore(PlatformDbContext db) : IWebhookDeliveryStore
{
    public async Task<Guid> EnqueueAsync(Guid webhookId, string eventType, Guid eventId, string payloadJson, DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform_api.webhook_deliveries
                (delivery_id, webhook_id, event_type, event_id, payload, status, attempt_count, created_at)
            VALUES (@p0, @p1, @p2, @p3, CAST(@p4 AS jsonb), 'pending', 0, @p5)
            """,
            new NpgsqlParameter("@p0", id),
            new NpgsqlParameter("@p1", webhookId),
            new NpgsqlParameter("@p2", eventType),
            new NpgsqlParameter("@p3", eventId),
            new NpgsqlParameter("@p4", payloadJson),
            new NpgsqlParameter("@p5", nowUtc));
        return id;
    }

    public Task MarkSuccessAsync(Guid deliveryId, string signature, int statusCode, int responseMs, short attempt, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE platform_api.webhook_deliveries
            SET status = 'success', signature = @p1, response_status_code = @p2, response_time_ms = @p3,
                attempt_count = @p4, delivered_at = @p5, next_retry_at = NULL
            WHERE delivery_id = @p0
            """,
            new NpgsqlParameter("@p0", deliveryId), new NpgsqlParameter("@p1", signature),
            new NpgsqlParameter("@p2", statusCode), new NpgsqlParameter("@p3", responseMs),
            new NpgsqlParameter("@p4", attempt), new NpgsqlParameter("@p5", nowUtc));

    public Task MarkFailedAsync(Guid deliveryId, string signature, int? statusCode, string error, short attempt, DateTime? nextRetryUtc, bool abandoned, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE platform_api.webhook_deliveries
            SET status = @p6, signature = @p1, response_status_code = @p2, error_message = @p3,
                attempt_count = @p4, next_retry_at = @p5
            WHERE delivery_id = @p0
            """,
            new NpgsqlParameter("@p0", deliveryId),
            new NpgsqlParameter("@p1", signature),
            new NpgsqlParameter("@p2", (object?)statusCode ?? DBNull.Value),
            new NpgsqlParameter("@p3", error),
            new NpgsqlParameter("@p4", attempt),
            new NpgsqlParameter("@p5", (object?)nextRetryUtc ?? DBNull.Value),
            new NpgsqlParameter("@p6", abandoned ? "abandoned" : "failed"));

    public Task<WebhookDelivery?> GetAsync(Guid deliveryId, CancellationToken ct) =>
        db.WebhookDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.DeliveryId == deliveryId, ct);
}

/// <summary>
/// Admin/forensics reads + manual retry over <c>platform_api.webhook_deliveries</c> (developer portal). Tenant
/// isolation is by an EXPLICIT predicate joined through <c>webhook_subscriptions.tenant_id</c> (the deliveries
/// table has no tenant_id); platform_api is non-RLS so this is the sole isolation boundary. Plain app-role SQL
/// (the blanket platform_api SELECT/UPDATE grant covers it) — no SECURITY DEFINER, no new grant.
/// </summary>
public sealed class WebhookDeliveryAdminStore(PlatformDbContext db) : IWebhookDeliveryAdminStore
{
    public async Task<IReadOnlyList<WebhookDeliveryDto>> ListByWebhookAsync(
        Guid webhookId, string? status, int take, Guid? tenantScope, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<WebhookDeliveryDto>(
                // Metadata only — payload/response_body/response_headers/signature are NEVER selected.
                """
                SELECT d.delivery_id AS "DeliveryId", d.webhook_id AS "WebhookId", d.event_type AS "EventType",
                       d.event_id AS "EventId", d.status AS "Status", d.attempt_count AS "AttemptCount",
                       d.response_status_code AS "ResponseStatusCode", d.response_time_ms AS "ResponseTimeMs",
                       d.error_message AS "ErrorMessage", d.next_retry_at AS "NextRetryAt",
                       d.created_at AS "CreatedAt", d.delivered_at AS "DeliveredAt"
                FROM platform_api.webhook_deliveries d
                JOIN platform_api.webhook_subscriptions s ON s.webhook_id = d.webhook_id
                WHERE d.webhook_id = @p_webhook
                  AND (@p_tenant::uuid IS NULL OR s.tenant_id = @p_tenant::uuid)
                  AND (@p_status::text IS NULL OR d.status = @p_status::text)
                ORDER BY d.created_at DESC
                LIMIT @p_take
                """,
                new NpgsqlParameter("@p_webhook", webhookId),
                new NpgsqlParameter("@p_tenant", (object?)tenantScope ?? DBNull.Value),
                new NpgsqlParameter("@p_status", (object?)status ?? DBNull.Value),
                new NpgsqlParameter("@p_take", take))
            .ToListAsync(ct);

    public async Task<RetryCandidate?> GetForRetryAsync(Guid deliveryId, Guid? tenantScope, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<RetryCandidate>(
                """
                SELECT d.status AS "Status", s.tenant_id AS "TenantId", s.is_active AS "SubscriptionActive",
                       (s.auto_disabled_at IS NOT NULL) AS "SubscriptionAutoDisabled"
                FROM platform_api.webhook_deliveries d
                JOIN platform_api.webhook_subscriptions s ON s.webhook_id = d.webhook_id
                WHERE d.delivery_id = @p_delivery
                  AND (@p_tenant::uuid IS NULL OR s.tenant_id = @p_tenant::uuid)
                """,
                new NpgsqlParameter("@p_delivery", deliveryId),
                new NpgsqlParameter("@p_tenant", (object?)tenantScope ?? DBNull.Value))
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    public async Task<WebhookDeliveryDto?> RetryAsync(Guid deliveryId, Guid? tenantScope, CancellationToken ct)
    {
        // ONE atomic conditional UPDATE: the status-IN('abandoned','failed') predicate is the dead-letter gate
        // (no-op vs success/processing/pending), the subscription join is the tenant + health gate, and a
        // 0-row result is the single-winner signal (raced by the drain) → the handler maps it to 409.
        var rows = await db.Database.SqlQueryRaw<WebhookDeliveryDto>(
                """
                UPDATE platform_api.webhook_deliveries d
                SET status = 'pending', attempt_count = 0, next_retry_at = NULL, error_message = NULL,
                    response_status_code = NULL, response_time_ms = NULL, response_body = NULL,
                    response_headers = NULL, signature = NULL
                FROM platform_api.webhook_subscriptions s
                WHERE d.delivery_id = @p_delivery
                  AND s.webhook_id = d.webhook_id
                  AND d.status IN ('abandoned', 'failed')
                  AND s.is_active AND s.auto_disabled_at IS NULL
                  AND (@p_tenant::uuid IS NULL OR s.tenant_id = @p_tenant::uuid)
                RETURNING d.delivery_id AS "DeliveryId", d.webhook_id AS "WebhookId", d.event_type AS "EventType",
                          d.event_id AS "EventId", d.status AS "Status", d.attempt_count AS "AttemptCount",
                          d.response_status_code AS "ResponseStatusCode", d.response_time_ms AS "ResponseTimeMs",
                          d.error_message AS "ErrorMessage", d.next_retry_at AS "NextRetryAt",
                          d.created_at AS "CreatedAt", d.delivered_at AS "DeliveredAt"
                """,
                new NpgsqlParameter("@p_delivery", deliveryId),
                new NpgsqlParameter("@p_tenant", (object?)tenantScope ?? DBNull.Value))
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }
}
