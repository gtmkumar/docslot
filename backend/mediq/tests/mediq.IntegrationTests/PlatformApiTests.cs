using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using mediq.Application.Abstractions;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.PlatformApi;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice-02 platform_api invariants against the live canonical DB:
///   client-credentials happy path → scoped endpoint 200, scope denial → 403, token revocation → 401/403,
///   and a webhook signed-delivery + retry dry-run (HMAC correctness + Polly retry on simulated failure).
/// </summary>
public sealed class PlatformApiTests(PlatformApiWebAppFactory factory) : IClassFixture<PlatformApiWebAppFactory>
{
    // ---- B/C: OAuth client-credentials + scope enforcement -------------------------------------

    [Fact]
    public async Task ClientCredentials_Token_Then_Call_Scoped_Endpoint_Succeeds()
    {
        var client = factory.CreateClient();

        var token = await GetClientTokenAsync(client, "docslot.bookings.read");
        Assert.False(string.IsNullOrWhiteSpace(token.AccessToken));
        Assert.Equal("Bearer", token.TokenType);
        Assert.Contains("docslot.bookings.read", token.Scope);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var resp = await client.GetAsync("/api/v1/public/bookings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Token_Lacking_Scope_Is_Denied_403()
    {
        var client = factory.CreateClient();

        // The client is NOT granted docslot.patients.read; request only bookings.read.
        var token = await GetClientTokenAsync(client, "docslot.bookings.read");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var resp = await client.GetAsync("/api/v1/public/patients");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Requesting_Ungranted_Scope_Is_Rejected()
    {
        var client = factory.CreateClient();
        // Explicitly request a scope the client does not hold → invalid_scope (422 business rule).
        var resp = await client.PostAsJsonAsync("/api/v1/oauth/token", new OAuthTokenRequest(
            "client_credentials", factory.ClientCode, PlatformApiWebAppFactory.ClientSecret, "docslot.patients.read"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Bad_Secret_Is_Rejected_As_Invalid_Client()
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/oauth/token", new OAuthTokenRequest(
            "client_credentials", factory.ClientCode, "wrong-secret"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Revoked_Token_Cannot_Call_Scoped_Endpoint()
    {
        var client = factory.CreateClient();
        var token = await GetClientTokenAsync(client, "docslot.bookings.read");

        // Sanity: works before revoke.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/public/bookings")).StatusCode);

        // Revoke, then the same token must fail closed (scope context empty → 403).
        var revokeClient = factory.CreateClient();
        var revoke = await revokeClient.PostAsJsonAsync("/api/v1/oauth/revoke", new OAuthRevokeRequest(token.AccessToken));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var after = await client.GetAsync("/api/v1/public/bookings");
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
    }

    // ---- E: webhook DURABLE ASYNC delivery (publish enqueues; the worker drains + retries) ------

    [Fact]
    public async Task Webhook_Publish_Enqueues_Only_Then_Worker_Drains_With_Retry_And_Hmac()
    {
        var admin = factory.CreateClient();
        var adminToken = await AdminLoginAsync(admin);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken.AccessToken);

        const string secret = "webhook-known-secret-abcdef";
        var create = await admin.PostAsJsonAsync("/api/v1/webhooks", new CreateWebhookRequest(
            factory.ClientId, TenantId: null, Name: "test-hook", Url: "https://127.0.0.1:9/hook",
            EventTypes: new[] { "docslot.booking.created" }, Secret: secret, MaxRetries: 3));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<CreateWebhookResult>())!;

        try
        {
            PlatformApiWebAppFactory.Dispatcher.Reset();
            PlatformApiWebAppFactory.Dispatcher.FailFirst(2);   // attempts 1-2 fail → 3rd succeeds (proves retry)

            const string payload = "{\"booking_id\":\"abc\",\"status\":\"pending\"}";
            var publish = await admin.PostAsJsonAsync("/api/v1/webhooks/publish",
                new { eventType = "docslot.booking.created", tenantId = (Guid?)null, payloadJson = payload });
            Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

            // THE FIX — durable async: publishing did NOT deliver in-request (the dispatcher was never called);
            // the delivery is enqueued 'pending'. A slow/dead subscriber can no longer stall the request path.
            Assert.Equal(0, PlatformApiWebAppFactory.Dispatcher.AttemptCount);
            Assert.Equal("pending", await ScalarStrAsync(
                "SELECT status FROM platform_api.webhook_deliveries WHERE webhook_id=@w", ("w", created.WebhookId)));

            // Worker tick 1 → delivery attempt fails → 'failed', attempt_count=1, a backoff next_retry_at set.
            Assert.Equal(1, await DrainWebhooksOnceAsync());
            Assert.Equal("failed", await ScalarStrAsync(
                "SELECT status FROM platform_api.webhook_deliveries WHERE webhook_id=@w", ("w", created.WebhookId)));
            Assert.Equal(1, await ScalarIntAsync(
                "SELECT attempt_count FROM platform_api.webhook_deliveries WHERE webhook_id=@w", ("w", created.WebhookId)));

            // Ticks 2-3 → 2nd attempt fails, 3rd succeeds → 'success'.
            await DrainWebhooksOnceAsync();
            await DrainWebhooksOnceAsync();
            Assert.Equal("success", await ScalarStrAsync(
                "SELECT status FROM platform_api.webhook_deliveries WHERE webhook_id=@w", ("w", created.WebhookId)));
            Assert.True(PlatformApiWebAppFactory.Dispatcher.AttemptCount >= 3);

            // HMAC correctness: signature == HMAC-SHA256(deliveredPayload, secret) — exactly what a subscriber verifies.
            Assert.True(PlatformApiWebAppFactory.Dispatcher.Calls.TryPeek(out var call));
            var expected = "sha256=" + Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(call!.Payload))).ToLowerInvariant();
            Assert.Equal(expected, call.Signature);
            Assert.Contains("booking_id", call.Payload);
        }
        finally
        {
            await DeleteWebhookAsync(created.WebhookId);
        }
    }

    [Fact]
    public async Task Webhook_Delivery_Dead_Letters_After_Max_Retries()
    {
        var admin = factory.CreateClient();
        var adminToken = await AdminLoginAsync(admin);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken.AccessToken);

        const string secret = "webhook-deadletter-secret-1234";
        var create = await admin.PostAsJsonAsync("/api/v1/webhooks", new CreateWebhookRequest(
            factory.ClientId, TenantId: null, Name: "deadletter-hook", Url: "https://127.0.0.1:9/hook",
            EventTypes: new[] { "docslot.booking.created" }, Secret: secret, MaxRetries: 1));   // 1 retry → 2 attempts
        var created = (await create.Content.ReadFromJsonAsync<CreateWebhookResult>())!;

        try
        {
            PlatformApiWebAppFactory.Dispatcher.Reset();
            PlatformApiWebAppFactory.Dispatcher.FailFirst(100);   // always fail

            await admin.PostAsJsonAsync("/api/v1/webhooks/publish",
                new { eventType = "docslot.booking.created", tenantId = (Guid?)null, payloadJson = "{\"booking_id\":\"x\"}" });

            // 2 attempts (1 + 1 retry), both fail → 'abandoned' (dead-letter), not retried forever.
            await DrainWebhooksOnceAsync();   // attempt 1 → 'failed'
            await DrainWebhooksOnceAsync();   // attempt 2 → 'abandoned'
            Assert.Equal("abandoned", await ScalarStrAsync(
                "SELECT status FROM platform_api.webhook_deliveries WHERE webhook_id=@w", ("w", created.WebhookId)));
            Assert.Equal(2, await ScalarIntAsync(
                "SELECT attempt_count FROM platform_api.webhook_deliveries WHERE webhook_id=@w", ("w", created.WebhookId)));

            // An abandoned delivery is NOT re-claimed; the subscription's failure health is recorded.
            Assert.Equal(0, await DrainWebhooksOnceAsync());
            Assert.True(await ScalarIntAsync(
                "SELECT consecutive_failures FROM platform_api.webhook_subscriptions WHERE webhook_id=@w", ("w", created.WebhookId)) >= 2);
        }
        finally
        {
            await DeleteWebhookAsync(created.WebhookId);
        }
    }

    [Fact]
    public async Task Webhook_Stale_SingleWinner_Loser_Does_Not_Perturb_Subscription_Health()
    {
        var admin = factory.CreateClient();
        var adminToken = await AdminLoginAsync(admin);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken.AccessToken);

        const string secret = "webhook-loser-secret-9876";
        var create = await admin.PostAsJsonAsync("/api/v1/webhooks", new CreateWebhookRequest(
            factory.ClientId, TenantId: null, Name: "loser-hook", Url: "https://127.0.0.1:9/hook",
            EventTypes: new[] { "docslot.booking.created" }, Secret: secret, MaxRetries: 3));
        var created = (await create.Content.ReadFromJsonAsync<CreateWebhookResult>())!;

        try
        {
            PlatformApiWebAppFactory.Dispatcher.Reset();   // all attempts succeed
            await admin.PostAsJsonAsync("/api/v1/webhooks/publish",
                new { eventType = "docslot.booking.created", tenantId = (Guid?)null, payloadJson = "{\"booking_id\":\"ok\"}" });

            // Winner delivers → 'success', subscription consecutive_failures reset to 0.
            await DrainWebhooksOnceAsync();
            Assert.Equal("success", await ScalarStrAsync("SELECT status FROM platform_api.webhook_deliveries WHERE webhook_id=@w", ("w", created.WebhookId)));
            Assert.Equal(0, await ScalarIntAsync("SELECT consecutive_failures FROM platform_api.webhook_subscriptions WHERE webhook_id=@w", ("w", created.WebhookId)));

            // A STALE LOSER — a late MarkFailed on a row that is no longer 'processing' (lease-collision twin).
            // The delivery UPDATE matches 0 rows, so the GATED subscription-health UPDATE must be a no-op: the
            // delivery stays 'success' and consecutive_failures is NOT bumped toward auto-disable (auditor finding).
            var deliveryId = Guid.Parse((await ScalarStrAsync(
                "SELECT delivery_id::text FROM platform_api.webhook_deliveries WHERE webhook_id=@w", ("w", created.WebhookId)))!);
            using (var scope = factory.Services.CreateScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IWebhookDeliveryDrainStore>();
                await store.MarkFailedAsync(deliveryId, "sig", 500, "stale loser", maxRetries: 3,
                    autoDisableThreshold: 20, DateTime.UtcNow.AddSeconds(60), DateTime.UtcNow, default);
            }
            Assert.Equal("success", await ScalarStrAsync("SELECT status FROM platform_api.webhook_deliveries WHERE webhook_id=@w", ("w", created.WebhookId)));
            Assert.Equal(0, await ScalarIntAsync("SELECT consecutive_failures FROM platform_api.webhook_subscriptions WHERE webhook_id=@w", ("w", created.WebhookId)));
        }
        finally
        {
            await DeleteWebhookAsync(created.WebhookId);
        }
    }

    /// <summary>One drain pass: mirrors WebhookDeliveryWorker.DrainOnce against the live DB via the real drain
    /// store + signer + the fake dispatcher. A failed delivery is rescheduled in the PAST so the next pass
    /// re-claims it immediately (fast retry for the test, vs the worker's real exponential backoff).</summary>
    private async Task<int> DrainWebhooksOnceAsync()
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IWebhookDeliveryDrainStore>();
        var signer = scope.ServiceProvider.GetRequiredService<IWebhookSigner>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IWebhookHttpDispatcher>();

        var batch = await store.ClaimDueAsync(20, leaseSeconds: 300, DateTime.UtcNow, default);
        foreach (var d in batch)
        {
            var sig = signer.SignWithProtected(d.PayloadJson, d.SecretHash);
            var r = await dispatcher.PostAsync(d.Url, d.PayloadJson, sig, d.TimeoutSeconds, default);
            if (r.Success)
                await store.MarkDeliveredAsync(d.DeliveryId, sig, r.StatusCode ?? 200, r.ElapsedMs, DateTime.UtcNow, default);
            else
                await store.MarkFailedAsync(d.DeliveryId, sig, r.StatusCode, r.Error ?? "fail",
                    d.MaxRetries, autoDisableThreshold: 20, DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow, default);
        }
        return batch.Count;
    }

    private static Task DeleteWebhookAsync(Guid webhookId) =>
        ExecAsync("DELETE FROM platform_api.webhook_deliveries WHERE webhook_id=@w; DELETE FROM platform_api.webhook_subscriptions WHERE webhook_id=@w", ("w", webhookId));

    private static async Task<int> ScalarIntAsync(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(PlatformApiWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string?> ScalarStrAsync(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(PlatformApiWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    private static async Task ExecAsync(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(PlatformApiWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<OAuthTokenResponse> GetClientTokenAsync(HttpClient client, string scope)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/oauth/token", new OAuthTokenRequest(
            "client_credentials", factory.ClientCode, PlatformApiWebAppFactory.ClientSecret, scope));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<OAuthTokenResponse>())!;
    }

    private async Task<TokenResponse> AdminLoginAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.AdminEmail, PlatformApiWebAppFactory.AdminPassword));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
    }
}
