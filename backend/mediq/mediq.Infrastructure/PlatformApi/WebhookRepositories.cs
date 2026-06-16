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

    public async Task<IReadOnlyList<WebhookSubscriptionDto>> ListByClientAsync(Guid clientId, CancellationToken ct) =>
        await db.WebhookSubscriptions.AsNoTracking()
            .Where(w => w.ClientId == clientId)
            .OrderBy(w => w.Name)
            .Select(w => new WebhookSubscriptionDto(
                w.WebhookId, w.ClientId, w.TenantId, w.Name, w.Url, w.EventTypes, w.MaxRetries,
                w.RetryBackoff, w.TimeoutSeconds, w.IsActive, w.ConsecutiveFailures,
                w.LastSuccessAt, w.LastFailureAt, w.AutoDisabledAt, w.CreatedAt))
            .ToListAsync(ct);

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
