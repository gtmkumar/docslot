using mediq.Application.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Fixture for the durable integration-event OUTBOX (phase-4 seam). Boots the real API against the live
/// canonical DB and seeds a single tenant (the outbox's <c>tenant_id</c> FK target). The integration-event
/// drain worker is DEFAULT-OFF (and force-off via TestHostConfig), so each test drives the stores explicitly
/// (<see cref="IIntegrationOutboxStore"/> / <see cref="IIntegrationEventOutboxDrainStore"/>) or exercises the
/// transactional capture tap through the real <see cref="IWebhookPublisher"/> — no background timing flakiness.
/// Provider stays "none" so the bus is the <c>NullIntegrationEventBus</c> (no broker needed).
/// </summary>
public sealed class IntegrationOutboxWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";

    public Guid TenantId { get; } = Guid.NewGuid();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            // Belt-and-suspenders: no background hosted services in this fixture (the test owns all timing).
            services.RemoveAll(typeof(Microsoft.Extensions.Hosting.IHostedService));
        });
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (@id, @code, 'Outbox Test', 'Outbox Test', 'hospital', 'outbox@docslot.test', '+919000000099', 'active')
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            ("id", TenantId), ("code", $"obx-{TenantId.ToString()[..8]}"));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await Exec(conn, "DELETE FROM platform_api.integration_event_outbox WHERE tenant_id = @t", ("t", TenantId));
        // Webhook FK chain (deliveries → subscriptions → clients) seeded by the retention prune tests, by tenant.
        await Exec(conn,
            """
            DELETE FROM platform_api.webhook_deliveries d
            USING platform_api.webhook_subscriptions s
            WHERE d.webhook_id = s.webhook_id AND s.tenant_id = @t
            """, ("t", TenantId));
        await Exec(conn, "DELETE FROM platform_api.webhook_subscriptions WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM platform_api.api_clients WHERE owner_tenant_id = @t", ("t", TenantId));
        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = @t", ("t", TenantId));
        await base.DisposeAsync();
    }

    // ---- direct outbox-row helpers (drive the store tests deterministically) -------------------------------

    /// <summary>Inserts a 'pending' outbox row directly (bypassing the tap) so the drain-store tests have a row
    /// to claim. Optional <paramref name="status"/> / <paramref name="nextRetryAt"/> let a test seed a row that
    /// is already 'processing' (lease recovery) or 'failed' (re-claim after backoff). <paramref name="publishedAt"/>
    /// lets a test seed a terminal <c>status='success'</c> row with a PAST completion stamp (so the retention
    /// pruner can age it out) — success rows always carry published_at in production.</summary>
    public async Task<Guid> InsertRowAsync(
        Guid? eventId = null, string eventType = "docslot.booking.created",
        string status = "pending", DateTime? nextRetryAt = null, int attemptCount = 0, DateTime? publishedAt = null)
    {
        var outboxId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO platform_api.integration_event_outbox
                (outbox_id, event_id, event_type, tenant_id, payload, correlation_id, occurred_at, status, attempt_count, next_retry_at, published_at, created_at)
            VALUES (@id, @eid, @etype, @tid, '{"data":{}}'::jsonb, @corr, NOW(), @status, @attempt, @next, @published, NOW())
            """, conn);
        cmd.Parameters.AddWithValue("id", outboxId);
        cmd.Parameters.AddWithValue("eid", eventId ?? Guid.NewGuid());
        cmd.Parameters.AddWithValue("etype", eventType);
        cmd.Parameters.AddWithValue("tid", TenantId);
        cmd.Parameters.AddWithValue("corr", $"corr-{outboxId:N}");
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("attempt", attemptCount);
        cmd.Parameters.AddWithValue("next", (object?)nextRetryAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("published", (object?)publishedAt ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return outboxId;
    }

    // ---- webhook-delivery helpers (retention prune tests) --------------------------------------------------

    private Guid? _webhookId;

    /// <summary>Ensures one <c>api_clients</c> + <c>webhook_subscriptions</c> row exists for this fixture's
    /// tenant (the delivery FK target), creating them lazily on first use. Both are tagged with this tenant so
    /// DisposeAsync can clean them by tenant_id.</summary>
    private async Task<Guid> EnsureWebhookSubscriptionAsync(NpgsqlConnection conn)
    {
        if (_webhookId is { } existing) return existing;

        var clientId = Guid.NewGuid();
        var webhookId = Guid.NewGuid();
        await using (var clientCmd = new NpgsqlCommand(
            """
            INSERT INTO platform_api.api_clients
                (client_id, client_code, client_name, client_secret_hash, client_type, owner_tenant_id, owner_email, purpose)
            VALUES (@cid, @code, 'Retention Test Client', 'x', 'first_party', @tid, @email, 'retention prune test')
            """, conn))
        {
            clientCmd.Parameters.AddWithValue("cid", clientId);
            clientCmd.Parameters.AddWithValue("code", $"ret-{clientId.ToString()[..8]}");
            clientCmd.Parameters.AddWithValue("tid", TenantId);
            clientCmd.Parameters.AddWithValue("email", $"ret-{clientId.ToString()[..8]}@docslot.test");
            await clientCmd.ExecuteNonQueryAsync();
        }

        await using (var subCmd = new NpgsqlCommand(
            """
            INSERT INTO platform_api.webhook_subscriptions
                (webhook_id, client_id, tenant_id, name, url, secret_hash, event_types)
            VALUES (@wid, @cid, @tid, 'retention-test', 'https://example.test/hook', 'x', ARRAY['docslot.booking.created'])
            """, conn))
        {
            subCmd.Parameters.AddWithValue("wid", webhookId);
            subCmd.Parameters.AddWithValue("cid", clientId);
            subCmd.Parameters.AddWithValue("tid", TenantId);
            await subCmd.ExecuteNonQueryAsync();
        }

        _webhookId = webhookId;
        return webhookId;
    }

    /// <summary>Inserts a webhook delivery row with the chosen <paramref name="status"/> and (optional)
    /// <paramref name="deliveredAt"/> completion stamp, seeding the api_client/subscription FK chain on first
    /// use. A <c>status='success'</c> row with a PAST delivered_at is what the retention pruner ages out.</summary>
    public async Task<Guid> InsertWebhookDeliveryAsync(string status, DateTime? deliveredAt, Guid? eventId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        var webhookId = await EnsureWebhookSubscriptionAsync(conn);

        var deliveryId = Guid.NewGuid();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO platform_api.webhook_deliveries
                (delivery_id, webhook_id, event_type, event_id, payload, status, attempt_count, created_at, delivered_at)
            VALUES (@id, @wid, 'docslot.booking.created', @eid, '{"data":{}}'::jsonb, @status, 0, NOW(), @delivered)
            """, conn);
        cmd.Parameters.AddWithValue("id", deliveryId);
        cmd.Parameters.AddWithValue("wid", webhookId);
        cmd.Parameters.AddWithValue("eid", eventId ?? Guid.NewGuid());
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("delivered", (object?)deliveredAt ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return deliveryId;
    }

    public async Task<int> CountWebhookDeliveryByEventIdAsync(Guid eventId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM platform_api.webhook_deliveries WHERE event_id = @e", conn);
        cmd.Parameters.AddWithValue("e", eventId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public async Task<(string Status, int AttemptCount, DateTime? NextRetryAt, DateTime? PublishedAt, string? LastError)> ReadAsync(Guid outboxId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT status, attempt_count, next_retry_at, published_at, last_error FROM platform_api.integration_event_outbox WHERE outbox_id = @id",
            conn);
        cmd.Parameters.AddWithValue("id", outboxId);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (
            reader.GetString(0),
            reader.GetInt16(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    public async Task<int> CountByEventIdAsync(Guid eventId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM platform_api.integration_event_outbox WHERE event_id = @e", conn);
        cmd.Parameters.AddWithValue("e", eventId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
