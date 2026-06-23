using mediq.Application.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Fixture for the OUTBOUND WhatsApp drain worker. Boots the real API against the live canonical DB, but:
/// <list type="bullet">
/// <item>disables the BACKGROUND <c>OutboxDrainWorker</c> hosted service (<c>WhatsApp:OutboxWorkerEnabled=false</c>)
/// so the test drives the drain deterministically by resolving <see cref="IOutboxDrainStore"/> /
/// <see cref="IWhatsAppSender"/> directly — no timing flakiness;</item>
/// <item>replaces <see cref="IWhatsAppSender"/> with a <see cref="ControllableSender"/> whose success/failure
/// the test toggles, so the retry → abandon path is exercisable without a real Meta endpoint.</item>
/// </list>
/// The factory only needs a tenant to attribute outbox rows to; it does not seed the booking graph.
/// </summary>
public sealed class WhatsAppOutboxWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";

    public Guid TenantId { get; } = Guid.NewGuid();

    /// <summary>The swapped-in sender the tests control (force success / failure).</summary>
    public ControllableSender Sender { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Disable the live background worker — the test owns the drain timing.
                ["WhatsApp:OutboxWorkerEnabled"] = "false",
                // Deterministic, short backoff so the retry path is observable.
                ["WhatsApp:BackoffBaseSeconds"] = "1",
                ["WhatsApp:BackoffMaxSeconds"] = "2",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace whatever sender Infrastructure wired (the stub) with our controllable one.
            services.RemoveAll<IWhatsAppSender>();
            services.AddSingleton<IWhatsAppSender>(Sender);

            // Belt-and-suspenders: ensure the background drain worker is NOT registered, so the test fully
            // owns the drain timing regardless of config layering.
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
        });
    }

    /// <summary>
    /// Runs one drain pass for a SINGLE target row through the real <see cref="IOutboxDrainStore"/> mark
    /// logic + the controllable sender. The claim is scoped to <paramref name="outboxId"/> (the production
    /// store claims cross-tenant; scoping here keeps parallel tests from claiming each other's rows). It still
    /// exercises the real status transition + RETURNING projection, and the real MarkSent/MarkFailed SQL.
    /// <paramref name="retryImmediately"/> schedules a failed row's next_retry_at in the PAST so the following
    /// drain re-claims it without a wall-clock wait.
    /// </summary>
    public async Task DrainOnceAsync(Guid outboxId, bool retryImmediately = false)
    {
        using var scope = Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxDrainStore>();
        var sender = scope.ServiceProvider.GetRequiredService<IWhatsAppSender>();

        var message = await ClaimSingleAsync(outboxId);
        if (message is null)
            return;   // not due / already terminal

        var result = await sender.SendAsync(message, default);
        if (result.Success && result.ProviderMessageId is { Length: > 0 })
            await store.MarkSentAsync(message.OutboxId, result.ProviderMessageId, DateTime.UtcNow, default);
        else
        {
            var nextRetry = retryImmediately ? DateTime.UtcNow.AddSeconds(-1) : DateTime.UtcNow.AddSeconds(60);
            await store.MarkFailedAsync(message.OutboxId, result.Error ?? "fail", nextRetry, DateTime.UtcNow, default);
        }
    }

    /// <summary>Test-scoped single-row claim: the same 'pending'→'processing' transition the store does, but for one id.</summary>
    private async Task<OutboundMessage?> ClaimSingleAsync(Guid outboxId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE docslot.outbox_messages
            SET status = 'processing'
            WHERE outbox_id = @id
              AND status = 'pending'
              AND (next_retry_at IS NULL OR next_retry_at <= now())
            RETURNING tenant_id, patient_id, message_intent, payload->>'to', payload->>'text',
                      correlation_id, attempt_count, max_attempts
            """, conn);
        cmd.Parameters.AddWithValue("id", outboxId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new OutboundMessage(
            outboxId,
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt16(6),
            reader.GetInt16(7));
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (@id, @code, 'WA Outbox Test', 'WA Outbox Test', 'hospital', 'waob@docslot.test', '+919000000001', 'active')
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            ("id", TenantId), ("code", $"waob-{TenantId.ToString()[..8]}"));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await Exec(conn, "DELETE FROM docslot.outbox_messages WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = @t", ("t", TenantId));
        await base.DisposeAsync();
    }

    /// <summary>Enqueue a 'pending' outbox row directly (bypassing the conversation flow) for drain tests.</summary>
    public async Task<Guid> EnqueuePendingAsync(string toPhone, string text, int maxAttempts = 5)
    {
        var outboxId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO docslot.outbox_messages
                (outbox_id, tenant_id, message_intent, payload, status, attempt_count, max_attempts, next_retry_at, created_at)
            VALUES (@id, @tid, 'test_message', @payload::jsonb, 'pending', 0, @max, NOW(), NOW())
            """, conn);
        cmd.Parameters.AddWithValue("id", outboxId);
        cmd.Parameters.AddWithValue("tid", TenantId);
        cmd.Parameters.AddWithValue("payload",
            System.Text.Json.JsonSerializer.Serialize(new { to = toPhone, text }));
        cmd.Parameters.AddWithValue("max", maxAttempts);
        await cmd.ExecuteNonQueryAsync();
        return outboxId;
    }

    public async Task<(string Status, int AttemptCount, string? WhatsAppMessageId, DateTime? SentAt)> ReadAsync(Guid outboxId)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT status, attempt_count, whatsapp_message_id, sent_at FROM docslot.outbox_messages WHERE outbox_id = @id",
            conn);
        cmd.Parameters.AddWithValue("id", outboxId);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3));
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A test double sender: returns success or a forced failure based on <see cref="FailNext"/>.</summary>
    public sealed class ControllableSender : IWhatsAppSender
    {
        public bool FailNext { get; set; }
        public int SendCount { get; private set; }

        public Task<WhatsAppSendResult> SendAsync(OutboundMessage message, CancellationToken ct)
        {
            SendCount++;
            return Task.FromResult(FailNext
                ? WhatsAppSendResult.Failed("forced test failure")
                : WhatsAppSendResult.Sent($"wamid.stub.{Guid.NewGuid():N}"));
        }
    }
}
