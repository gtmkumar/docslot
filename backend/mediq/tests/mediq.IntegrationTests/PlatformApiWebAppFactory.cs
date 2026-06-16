using System.Collections.Concurrent;
using mediq.Application.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice-02 fixture. Boots the real API against the live canonical DB and seeds:
///   - a super_admin user (to exercise the management endpoints, gated by platform.api_clients.manage),
///   - an approved api_client with a KNOWN secret and a granted scope set (docslot.bookings.read + slots),
///   - a webhook subscription is created per-test via the API.
/// It swaps <see cref="IWebhookHttpDispatcher"/> for a controllable fake so the webhook test can assert HMAC
/// correctness and simulate failures to prove retry. Cleanup soft-deletes audited rows (audit immutability).
/// </summary>
public sealed class PlatformApiWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string AdminPassword = "Sup3rSecret!";
    public const string ClientSecret = "slice02-known-secret-value-1234567890";

    public Guid AdminUserId { get; } = Guid.NewGuid();
    public Guid ClientId { get; } = Guid.NewGuid();
    public string AdminEmail { get; } = $"slice02.admin+{Guid.NewGuid():N}@docslot.test";
    public string ClientCode { get; } = $"slice02-client-{Guid.NewGuid():N}"[..28];

    public static readonly FakeWebhookDispatcher Dispatcher = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            // Swap the real HTTP transport for a controllable fake (records calls, can force failures).
            var original = services.Single(d => d.ServiceType == typeof(IWebhookHttpDispatcher));
            services.Remove(original);
            services.AddScoped<IWebhookHttpDispatcher>(_ => Dispatcher);
        });
    }

    public async Task InitializeAsync()
    {
        Dispatcher.Reset();
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        // super_admin user (bcrypt seed via pgcrypto) + platform-level assignment.
        await Exec(conn,
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, is_platform_user, created_at, updated_at)
            VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Slice02 Admin', true, true, NOW(), NOW())
            ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
            """,
            ("id", AdminUserId), ("email", AdminEmail), ("pwd", AdminPassword));

        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, NULL, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = 'super_admin' AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", AdminUserId));

        // An approved, active api_client (first_party so it can issue tokens immediately) with a KNOWN secret.
        await Exec(conn,
            """
            INSERT INTO platform_api.api_clients
                (client_id, client_code, client_name, client_secret_hash, client_type, owner_email, purpose,
                 is_active, is_verified, rate_limit_per_minute, created_at, updated_at)
            VALUES (@id, @code, 'Slice02 Test Client',
                    crypt(@secret, gen_salt('bf', 10)), 'first_party', 'client.slice02@docslot.test',
                    'integration test', true, true, 1000, NOW(), NOW())
            ON CONFLICT (client_id) DO NOTHING
            """,
            ("id", ClientId), ("code", ClientCode), ("secret", ClientSecret));

        // Grant the client two scopes (bookings.read + slots.read) — patients.read is deliberately NOT granted.
        await Exec(conn,
            """
            INSERT INTO platform_api.api_client_scopes (client_id, scope_id, granted_at)
            SELECT @cid, s.scope_id, NOW() FROM platform_api.api_scopes s
            WHERE s.scope_key IN ('docslot.bookings.read', 'docslot.slots.read')
            ON CONFLICT DO NOTHING
            """,
            ("cid", ClientId));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        // Webhook + token + scope rows cascade/clean; api_requests reference the client → soft-handle.
        await Exec(conn, "DELETE FROM platform_api.webhook_deliveries WHERE webhook_id IN (SELECT webhook_id FROM platform_api.webhook_subscriptions WHERE client_id = @c)", ("c", ClientId));
        await Exec(conn, "DELETE FROM platform_api.webhook_subscriptions WHERE client_id = @c", ("c", ClientId));
        await Exec(conn, "DELETE FROM platform_api.api_tokens WHERE client_id = @c", ("c", ClientId));
        await Exec(conn, "DELETE FROM platform_api.api_client_scopes WHERE client_id = @c", ("c", ClientId));
        await Exec(conn, "DELETE FROM platform_api.api_requests WHERE client_id = @c", ("c", ClientId));
        await Exec(conn, "DELETE FROM platform_api.api_clients WHERE client_id = @c", ("c", ClientId));
        // The admin user is pinned by immutable audit_log rows → soft-delete + anonymize.
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = @u", ("u", AdminUserId));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = @u", ("u", AdminUserId));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = @e", ("e", AdminEmail));
        await Exec(conn, "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
            ("anon", $"deleted+{AdminUserId}@slice02.test"), ("u", AdminUserId));
        await base.DisposeAsync();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Controllable webhook transport. Records every call (url, payload, signature) and can be told to fail a
/// number of initial attempts (to prove Polly retry) before succeeding.
/// </summary>
public sealed class FakeWebhookDispatcher : IWebhookHttpDispatcher
{
    public sealed record Call(string Url, string Payload, string Signature);

    public ConcurrentQueue<Call> Calls { get; } = new();
    private int _failFirstN;
    private int _attempts;

    public void Reset() { Calls.Clear(); _failFirstN = 0; _attempts = 0; }
    public void FailFirst(int n) { _failFirstN = n; _attempts = 0; }
    public int AttemptCount => _attempts;

    public Task<WebhookHttpResult> PostAsync(string url, string payload, string signature, int timeoutSeconds, CancellationToken ct)
    {
        Calls.Enqueue(new Call(url, payload, signature));
        var attempt = Interlocked.Increment(ref _attempts);
        return Task.FromResult(attempt <= _failFirstN
            ? new WebhookHttpResult(false, 500, 1, "simulated failure")
            : new WebhookHttpResult(true, 200, 1, null));
    }
}
