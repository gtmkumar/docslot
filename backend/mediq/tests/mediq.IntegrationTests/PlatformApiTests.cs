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

    // ---- G: webhook deliveries forensics list + manual retry (developer portal, slice 12) --------

    [Fact]
    public async Task Webhook_Deliveries_List_Returns_Metadata_For_Webhook_Newest_First()
    {
        var admin = factory.CreateClient();
        var adminToken = await AdminLoginAsync(admin);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken.AccessToken);

        var create = await admin.PostAsJsonAsync("/api/v1/webhooks", new CreateWebhookRequest(
            factory.ClientId, TenantId: null, Name: "list-hook", Url: "https://example.test/hook",
            EventTypes: new[] { "docslot.booking.created" }, Secret: "list-secret-123456", MaxRetries: 3));
        var created = (await create.Content.ReadFromJsonAsync<CreateWebhookResult>())!;
        try
        {
            await SeedDeliveryAsync(created.WebhookId, "success", attempt: 1);
            await SeedDeliveryAsync(created.WebhookId, "abandoned", attempt: 4);

            var resp = await admin.GetAsync($"/api/v1/webhooks/{created.WebhookId}/deliveries");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var rows = (await resp.Content.ReadFromJsonAsync<List<WebhookDeliveryDto>>())!;
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(created.WebhookId, r.WebhookId));
            // Newest-first ordering (the abandoned row was inserted last).
            Assert.True(rows[0].CreatedAt >= rows[1].CreatedAt);
            // Metadata only — the JSON envelope never carries the payload/response body/signature.
            var raw = await resp.Content.ReadAsStringAsync();
            Assert.DoesNotContain("booking_id", raw);
            Assert.DoesNotContain("signature", raw, StringComparison.OrdinalIgnoreCase);

            // Status filter narrows the set.
            var failedOnly = await admin.GetAsync($"/api/v1/webhooks/{created.WebhookId}/deliveries?status=abandoned");
            var filtered = (await failedOnly.Content.ReadFromJsonAsync<List<WebhookDeliveryDto>>())!;
            Assert.Single(filtered);
            Assert.Equal("abandoned", filtered[0].Status);
        }
        finally { await DeleteWebhookAsync(created.WebhookId); }
    }

    [Fact]
    public async Task Webhook_Delivery_Retry_ReEnqueues_DeadLetter_And_Guards_State()
    {
        var admin = factory.CreateClient();
        var adminToken = await AdminLoginAsync(admin);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken.AccessToken);

        var create = await admin.PostAsJsonAsync("/api/v1/webhooks", new CreateWebhookRequest(
            factory.ClientId, TenantId: null, Name: "retry-hook", Url: "https://example.test/hook",
            EventTypes: new[] { "docslot.booking.created" }, Secret: "retry-secret-123456", MaxRetries: 3));
        var created = (await create.Content.ReadFromJsonAsync<CreateWebhookResult>())!;
        try
        {
            // (a) An ABANDONED (dead-lettered) delivery re-enqueues → 200, status 'pending', attempt_count reset.
            var abandoned = await SeedDeliveryAsync(created.WebhookId, "abandoned", attempt: 4);
            var retry = await admin.PostAsync($"/api/v1/webhooks/deliveries/{abandoned}/retry", null);
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            var dto = (await retry.Content.ReadFromJsonAsync<WebhookDeliveryDto>())!;
            Assert.Equal("pending", dto.Status);
            Assert.Equal(0, dto.AttemptCount);
            Assert.Null(dto.NextRetryAt);
            Assert.Equal("pending", await ScalarStrAsync("SELECT status FROM platform_api.webhook_deliveries WHERE delivery_id=@d", ("d", abandoned)));
            // The retry was audited.
            Assert.True(await ScalarIntAsync(
                "SELECT COUNT(*)::int FROM platform.audit_log WHERE resource_type='webhook_delivery' AND action='retry' AND resource_id=@d", ("d", abandoned)) >= 1);

            // (b) A 'failed' delivery is also retryable.
            var failed = await SeedDeliveryAsync(created.WebhookId, "failed", attempt: 1);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/webhooks/deliveries/{failed}/retry", null)).StatusCode);

            // (c) A 'success' delivery is NOT retryable (no double-delivery) → 409.
            var success = await SeedDeliveryAsync(created.WebhookId, "success", attempt: 1);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/webhooks/deliveries/{success}/retry", null)).StatusCode);

            // (d) An in-flight 'processing' delivery is NOT retryable (lease race) → 409.
            var processing = await SeedDeliveryAsync(created.WebhookId, "processing", attempt: 1);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/webhooks/deliveries/{processing}/retry", null)).StatusCode);

            // (e) An unknown delivery → 404.
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/v1/webhooks/deliveries/{Guid.NewGuid()}/retry", null)).StatusCode);
        }
        finally { await DeleteWebhookAsync(created.WebhookId); }
    }

    [Fact]
    public async Task Webhook_Delivery_Retry_Refused_When_Subscription_AutoDisabled()
    {
        var admin = factory.CreateClient();
        var adminToken = await AdminLoginAsync(admin);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken.AccessToken);

        var create = await admin.PostAsJsonAsync("/api/v1/webhooks", new CreateWebhookRequest(
            factory.ClientId, TenantId: null, Name: "disabled-hook", Url: "https://example.test/hook",
            EventTypes: new[] { "docslot.booking.created" }, Secret: "disabled-secret-123456", MaxRetries: 3));
        var created = (await create.Content.ReadFromJsonAsync<CreateWebhookResult>())!;
        try
        {
            var abandoned = await SeedDeliveryAsync(created.WebhookId, "abandoned", attempt: 4);
            await ExecAsync("UPDATE platform_api.webhook_subscriptions SET auto_disabled_at = NOW() WHERE webhook_id=@w", ("w", created.WebhookId));

            // The drain would never re-claim a delivery on an auto-disabled subscription → refuse the retry (409).
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/webhooks/deliveries/{abandoned}/retry", null)).StatusCode);
            Assert.Equal("abandoned", await ScalarStrAsync("SELECT status FROM platform_api.webhook_deliveries WHERE delivery_id=@d", ("d", abandoned)));
        }
        finally { await DeleteWebhookAsync(created.WebhookId); }
    }

    [Fact]
    public async Task Webhook_Delivery_AdminStore_Is_Tenant_Scoped_Across_Tenants()
    {
        // platform.api_clients.manage is platform-level (super_admin → tenantScope null → sees all), so the
        // tenant predicate is proven at the STORE level: a scope of tenant A must NOT see/retry tenant B's rows.
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var (whA, delA) = (Guid.NewGuid(), Guid.NewGuid());
        var (whB, delB) = (Guid.NewGuid(), Guid.NewGuid());
        try
        {
            foreach (var (tid, code) in new[] { (tenantA, "a"), (tenantB, "b") })
                await ExecAsync(
                    """
                    INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
                    VALUES (@id, @code, 'Slice12', 'Slice12', 'hospital', @code||'@s12.test', '+919600000000', 'active')
                    """, ("id", tid), ("code", $"s12-{code}-{tid.ToString()[..8]}"));
            await SeedTenantWebhookWithDeliveryAsync(whA, factory.ClientId, tenantA, delA, "abandoned");
            await SeedTenantWebhookWithDeliveryAsync(whB, factory.ClientId, tenantB, delB, "abandoned");

            using var scope = factory.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IWebhookDeliveryAdminStore>();

            // List: scoped to A sees A's delivery, NOT B's.
            Assert.Single(await store.ListByWebhookAsync(whA, null, 200, tenantA, default));
            Assert.Empty(await store.ListByWebhookAsync(whB, null, 200, tenantA, default));   // cross-tenant excluded
            // Retry/pre-check: a tenant-A scope cannot reach B's delivery (→ would be 404 / no re-enqueue).
            Assert.Null(await store.GetForRetryAsync(delB, tenantA, default));
            Assert.Null(await store.RetryAsync(delB, tenantA, default));
            Assert.Equal("abandoned", await ScalarStrAsync("SELECT status FROM platform_api.webhook_deliveries WHERE delivery_id=@d", ("d", delB)));
            // Positive control: tenant-B scope CAN re-enqueue B's own delivery.
            Assert.NotNull(await store.RetryAsync(delB, tenantB, default));
            Assert.Equal("pending", await ScalarStrAsync("SELECT status FROM platform_api.webhook_deliveries WHERE delivery_id=@d", ("d", delB)));
        }
        finally
        {
            await ExecAsync("DELETE FROM platform_api.webhook_deliveries WHERE webhook_id IN (@a,@b)", ("a", whA), ("b", whB));
            await ExecAsync("DELETE FROM platform_api.webhook_subscriptions WHERE webhook_id IN (@a,@b)", ("a", whA), ("b", whB));
            await ExecAsync("UPDATE platform.tenants SET deleted_at=NOW(), status='archived' WHERE tenant_id IN (@a,@b)", ("a", tenantA), ("b", tenantB));
        }
    }

    [Fact]
    public async Task CreateWebhook_Binds_Tenant_From_Jwt_Ignoring_Body_TenantId()
    {
        var admin = factory.CreateClient();   // super_admin, no tenant → JWT tenant is NULL
        var adminToken = await AdminLoginAsync(admin);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken.AccessToken);

        // The body tries to forge a tenant_id; the handler MUST ignore it and bind from the JWT (null here).
        var forged = Guid.NewGuid();
        var create = await admin.PostAsJsonAsync("/api/v1/webhooks", new CreateWebhookRequest(
            factory.ClientId, TenantId: forged, Name: "bind-hook", Url: "https://example.test/hook",
            EventTypes: new[] { "docslot.booking.created" }, Secret: "bind-secret-123456", MaxRetries: 3));
        var created = (await create.Content.ReadFromJsonAsync<CreateWebhookResult>())!;
        try
        {
            // The stored tenant_id is NULL (from the JWT), NOT the forged body value.
            Assert.Null(await ScalarStrAsync("SELECT tenant_id::text FROM platform_api.webhook_subscriptions WHERE webhook_id=@w", ("w", created.WebhookId)));
        }
        finally { await DeleteWebhookAsync(created.WebhookId); }
    }

    [Fact]
    public async Task CreateWebhook_Plaintext_Secret_Is_Not_Persisted_To_Idempotency_Store()
    {
        var admin = factory.CreateClient();
        var adminToken = await AdminLoginAsync(admin);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken.AccessToken);

        const string secret = "donotcache-secret-abcdef-1234567890";
        var idemKey = $"slice12-idem-{Guid.NewGuid():N}";
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks")
        {
            Content = JsonContent.Create(new CreateWebhookRequest(
                factory.ClientId, TenantId: null, Name: "idem-hook", Url: "https://example.test/hook",
                EventTypes: new[] { "docslot.booking.created" }, Secret: secret, MaxRetries: 3)),
        };
        req.Headers.Add("Idempotency-Key", idemKey);
        var resp = await admin.SendAsync(req);
        var created = (await resp.Content.ReadFromJsonAsync<CreateWebhookResult>())!;
        try
        {
            Assert.Equal(secret, created.SigningSecret);   // returned plaintext once, to the caller
            // IDoNotCacheResponse bypasses the idempotency store entirely → NO row for this key, and the
            // plaintext secret is nowhere in the (plaintext) idempotency table.
            Assert.Equal(0, await ScalarIntAsync(
                "SELECT COUNT(*)::int FROM platform.idempotency_keys WHERE idempotency_key=@k", ("k", idemKey)));
            Assert.Equal(0, await ScalarIntAsync(
                "SELECT COUNT(*)::int FROM platform.idempotency_keys WHERE response_payload LIKE @s", ("s", $"%{secret}%")));
        }
        finally { await DeleteWebhookAsync(created.WebhookId); }
    }

    /// <summary>Seeds a delivery row directly in a chosen terminal/in-flight status (for the list/retry tests).</summary>
    private static async Task<Guid> SeedDeliveryAsync(Guid webhookId, string status, short attempt)
    {
        var id = Guid.NewGuid();
        await ExecAsync(
            """
            INSERT INTO platform_api.webhook_deliveries
                (delivery_id, webhook_id, event_type, event_id, payload, status, attempt_count, created_at,
                 next_retry_at, delivered_at)
            VALUES (@id, @w, 'docslot.booking.created', @eid, '{"booking_id":"seed"}'::jsonb, @st, @att, NOW(),
                    CASE WHEN @st='processing' THEN NOW() + INTERVAL '5 min' ELSE NULL END,
                    CASE WHEN @st='success' THEN NOW() ELSE NULL END)
            """,
            ("id", id), ("w", webhookId), ("eid", Guid.NewGuid()), ("st", status), ("att", (int)attempt));
        return id;
    }

    /// <summary>Seeds a tenant-scoped webhook subscription + one delivery (for the store-level tenant-scope test).</summary>
    private static async Task SeedTenantWebhookWithDeliveryAsync(Guid webhookId, Guid clientId, Guid tenantId, Guid deliveryId, string status)
    {
        await ExecAsync(
            """
            INSERT INTO platform_api.webhook_subscriptions
                (webhook_id, client_id, tenant_id, name, url, secret_hash, event_types, max_retries,
                 timeout_seconds, is_active, created_at, updated_at)
            VALUES (@w, @c, @t, 'tenant-hook', 'https://example.test/hook', 'enc', ARRAY['docslot.booking.created'],
                    3, 30, true, NOW(), NOW())
            """,
            ("w", webhookId), ("c", clientId), ("t", tenantId));
        await ExecAsync(
            """
            INSERT INTO platform_api.webhook_deliveries
                (delivery_id, webhook_id, event_type, event_id, payload, status, attempt_count, created_at)
            VALUES (@id, @w, 'docslot.booking.created', @eid, '{"booking_id":"seed"}'::jsonb, @st, 4, NOW())
            """,
            ("id", deliveryId), ("w", webhookId), ("eid", Guid.NewGuid()), ("st", status));
    }

    // ---- F: per-client API rate limiting (api_clients.rate_limit_per_minute / _per_day → 429) ----

    [Fact]
    public async Task ApiClient_Exceeding_PerMinute_RateLimit_Gets_429_With_RetryAfter()
    {
        // Tighten this client's per-minute limit + start from a clean request window.
        await ExecAsync("UPDATE platform_api.api_clients SET rate_limit_per_minute = 2 WHERE client_id = @c", ("c", factory.ClientId));
        await ExecAsync("DELETE FROM platform_api.api_requests WHERE client_id = @c", ("c", factory.ClientId));
        try
        {
            var client = factory.CreateClient();
            var token = await GetClientTokenAsync(client, "docslot.bookings.read");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            // First 2 requests are within the limit.
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/public/bookings")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/public/bookings")).StatusCode);

            // The 3rd breaches the per-minute limit → 429 + Retry-After: 60.
            var limited = await client.GetAsync("/api/v1/public/bookings");
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal(60, limited.Headers.RetryAfter?.Delta?.TotalSeconds);

            // The rejected attempt was logged (status 429, error_code 'rate_limited') for abuse detection.
            Assert.True(await ScalarIntAsync(
                "SELECT COUNT(*)::int FROM platform_api.api_requests WHERE client_id=@c AND status_code=429 AND error_code='rate_limited'",
                ("c", factory.ClientId)) >= 1);
        }
        finally
        {
            await ExecAsync("UPDATE platform_api.api_clients SET rate_limit_per_minute = 1000, rate_limit_per_day = 10000 WHERE client_id = @c", ("c", factory.ClientId));
            await ExecAsync("DELETE FROM platform_api.api_requests WHERE client_id = @c", ("c", factory.ClientId));
        }
    }

    [Fact]
    public async Task ApiClient_Exceeding_PerDay_RateLimit_Gets_429()
    {
        // High minute limit (so the minute window can't trip) + a low DAY limit → exercise rate_limit_per_day.
        await ExecAsync("UPDATE platform_api.api_clients SET rate_limit_per_minute = 1000, rate_limit_per_day = 2 WHERE client_id = @c", ("c", factory.ClientId));
        await ExecAsync("DELETE FROM platform_api.api_requests WHERE client_id = @c", ("c", factory.ClientId));
        try
        {
            var client = factory.CreateClient();
            var token = await GetClientTokenAsync(client, "docslot.bookings.read");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/public/bookings")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/public/bookings")).StatusCode);

            // The 3rd breaches the per-DAY limit → 429 with the day-window Retry-After hint (3600).
            var limited = await client.GetAsync("/api/v1/public/bookings");
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal(3600, limited.Headers.RetryAfter?.Delta?.TotalSeconds);
        }
        finally
        {
            await ExecAsync("UPDATE platform_api.api_clients SET rate_limit_per_minute = 1000, rate_limit_per_day = 10000 WHERE client_id = @c", ("c", factory.ClientId));
            await ExecAsync("DELETE FROM platform_api.api_requests WHERE client_id = @c", ("c", factory.ClientId));
        }
    }

    // ---- #88: developer-portal read aggregates --------------------------------------------------

    [Fact]
    public async Task ApiClientList_Includes_Last24hRequestCount()
    {
        var admin = factory.CreateClient();
        var token = await AdminLoginAsync(admin);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // Deterministic window: clear this client's request log, then seed two rows inside 24h + one outside.
        await ExecAsync("DELETE FROM platform_api.api_requests WHERE client_id=@c", ("c", factory.ClientId));
        await SeedApiRequestAsync(hoursAgo: 1);
        await SeedApiRequestAsync(hoursAgo: 5);
        await SeedApiRequestAsync(hoursAgo: 48);   // outside the 24h window → excluded
        try
        {
            var clients = (await admin.GetFromJsonAsync<List<ApiClientDto>>("/api/v1/api-clients?take=200"))!;
            var c = clients.Single(x => x.ClientId == factory.ClientId);
            Assert.Equal(2, c.RequestsLast24h);
        }
        finally
        {
            await ExecAsync("DELETE FROM platform_api.api_requests WHERE client_id=@c", ("c", factory.ClientId));
        }
    }

    [Fact]
    public async Task WebhookList_Includes_Last7dDeliverySuccessRate_AndGuardsDivideByZero()
    {
        var admin = factory.CreateClient();
        var token = await AdminLoginAsync(admin);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var whRated = Guid.NewGuid();
        var whEmpty = Guid.NewGuid();
        try
        {
            await SeedWebhookAsync(whRated, "rate-hook-a");
            await SeedWebhookAsync(whEmpty, "rate-hook-b");
            // 3 success + 1 failed inside 7d → 0.75; one older success is outside the window (excluded).
            await SeedDeliveryAsync(whRated, "success", daysAgo: 1);
            await SeedDeliveryAsync(whRated, "success", daysAgo: 2);
            await SeedDeliveryAsync(whRated, "success", daysAgo: 3);
            await SeedDeliveryAsync(whRated, "failed", daysAgo: 1);
            await SeedDeliveryAsync(whRated, "success", daysAgo: 10);   // outside window → excluded

            var subs = (await admin.GetFromJsonAsync<List<WebhookSubscriptionDto>>(
                $"/api/v1/webhooks?clientId={factory.ClientId}"))!;

            var rated = subs.Single(s => s.WebhookId == whRated);
            Assert.NotNull(rated.DeliverySuccessRate7d);
            Assert.Equal(0.75, rated.DeliverySuccessRate7d!.Value, 3);   // 3 of 4 in-window deliveries succeeded

            var empty = subs.Single(s => s.WebhookId == whEmpty);
            Assert.Null(empty.DeliverySuccessRate7d);   // no deliveries → null (divide-by-zero guarded)
        }
        finally
        {
            await ExecAsync("DELETE FROM platform_api.webhook_deliveries WHERE webhook_id = ANY(@w)", ("w", new[] { whRated, whEmpty }));
            await ExecAsync("DELETE FROM platform_api.webhook_subscriptions WHERE webhook_id = ANY(@w)", ("w", new[] { whRated, whEmpty }));
        }
    }

    private Task SeedApiRequestAsync(int hoursAgo) =>
        ExecAsync(
            "INSERT INTO platform_api.api_requests (client_id, method, path, status_code, occurred_at) VALUES (@c,'GET','/x',200, NOW() - make_interval(hours => @h))",
            ("c", factory.ClientId), ("h", hoursAgo));

    private Task SeedWebhookAsync(Guid webhookId, string name) =>
        ExecAsync(
            """
            INSERT INTO platform_api.webhook_subscriptions
                (webhook_id, client_id, tenant_id, name, url, secret_hash, event_types, max_retries, timeout_seconds, is_active, created_at, updated_at)
            VALUES (@id, @c, NULL, @n, 'https://example.test/hook', 'x', ARRAY['booking.created'], 5, 30, true, NOW(), NOW())
            """,
            ("id", webhookId), ("c", factory.ClientId), ("n", name));

    private static Task SeedDeliveryAsync(Guid webhookId, string status, int daysAgo) =>
        ExecAsync(
            """
            INSERT INTO platform_api.webhook_deliveries (webhook_id, event_type, event_id, payload, status, attempt_count, created_at)
            VALUES (@w, 'booking.created', gen_random_uuid(), '{}'::jsonb, @s, 1, NOW() - make_interval(days => @d))
            """,
            ("w", webhookId), ("s", status), ("d", daysAgo));

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
