using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.PlatformApi;
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

    // ---- E: webhook signed-delivery + retry dry-run --------------------------------------------

    [Fact]
    public async Task Webhook_Delivery_Signs_With_Hmac_And_Retries_On_Failure()
    {
        var admin = factory.CreateClient();
        var adminToken = await AdminLoginAsync(admin);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken.AccessToken);

        const string secret = "webhook-known-secret-abcdef";
        var create = await admin.PostAsJsonAsync("/api/v1/webhooks", new CreateWebhookRequest(
            factory.ClientId, TenantId: null, Name: "test-hook", Url: "https://example.test/hook",
            EventTypes: new[] { "docslot.booking.created" }, Secret: secret, MaxRetries: 3));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<CreateWebhookResult>();
        Assert.NotNull(created);
        Assert.Equal(secret, created!.SigningSecret);   // plaintext returned once

        // Force the fake transport to fail the first 2 attempts → success on the 3rd (proves retry).
        PlatformApiWebAppFactory.Dispatcher.FailFirst(2);

        const string payload = "{\"booking_id\":\"abc\",\"status\":\"pending\"}";
        var publish = await admin.PostAsJsonAsync("/api/v1/webhooks/publish",
            new { eventType = "docslot.booking.created", tenantId = (Guid?)null, payloadJson = payload });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        // Retry happened: more than one attempt was made.
        Assert.True(PlatformApiWebAppFactory.Dispatcher.AttemptCount >= 3,
            $"expected >=3 attempts, got {PlatformApiWebAppFactory.Dispatcher.AttemptCount}");

        // HMAC correctness: the signature the dispatcher received equals HMAC-SHA256(payload, secret).
        Assert.True(PlatformApiWebAppFactory.Dispatcher.Calls.TryPeek(out var call));
        var expected = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        Assert.Equal(expected, call!.Signature);
        Assert.Equal(payload, call.Payload);
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
