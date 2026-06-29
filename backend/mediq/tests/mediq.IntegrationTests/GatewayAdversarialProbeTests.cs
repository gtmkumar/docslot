using System.Net;
using System.Net.Http.Headers;

namespace mediq.IntegrationTests;

/// <summary>
/// AUDITOR PROBE (security-compliance-auditor, trust-boundary merge gate). Exercises the path-traversal /
/// normalization / method-confusion bypass vectors that the delivered GatewayTrustBoundaryTests did not cover
/// explicitly. Each case asserts an attacker CANNOT reach a protected downstream path through an anonymous
/// allow-list route. If any of these fail, the gateway is bypassable and the merge must be vetoed.
/// </summary>
[Collection("Gateway")]
public sealed class GatewayAdversarialProbeTests(GatewayWebAppFactory factory) : IClassFixture<GatewayWebAppFactory>
{
    private readonly GatewayWebAppFactory _factory = factory;

    private HttpClient Client() =>
        _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

    // 1. Dot-segment / encoded-slash traversal: match the anon route but try to land on a protected path downstream.
    [Theory]
    [InlineData("/api/v1/auth/login/../bookings")]
    [InlineData("/api/v1/auth/login/..%2fbookings")]
    [InlineData("/api/v1/ref/abc/../../bookings")]
    public async Task Traversal_FromAnonRoute_CannotReachProtectedDownstreamUnauthed(string rawPath)
    {
        var before = _factory.Received.Count;
        var resp = await Client().SendAsync(new HttpRequestMessage(HttpMethod.Get, rawPath));

        // If the stub received anything, the forwarded path must NOT be a protected resource reached with no token.
        if (_factory.Received.Count > before)
        {
            var fwd = _factory.LastRequest!.Path;
            Assert.False(
                fwd.Equals("/api/v1/bookings", StringComparison.OrdinalIgnoreCase),
                $"BYPASS: anon route forwarded to protected '{fwd}' with no token (raw='{rawPath}', status={resp.StatusCode})");
        }
        Assert.True(resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.OK
            or HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"raw='{rawPath}' produced unexpected status {resp.StatusCode}");
    }

    // 2. Method confusion: anon routes pin a verb. A different verb on the same path must fall to the
    //    authenticated catch-all (401 with no token), NOT slip through anonymously.
    [Theory]
    [InlineData("DELETE", "/api/v1/auth/login")]
    [InlineData("PUT", "/api/v1/oauth/token")]
    [InlineData("GET", "/api/v1/auth/login")]      // login is POST-only on the anon route
    [InlineData("POST", "/api/v1/ref/abc123")]     // ref is GET-only on the anon route
    public async Task MethodConfusion_OnAnonRoute_FallsToAuthCatchAll(string method, string path)
    {
        var before = _factory.Received.Count;
        var resp = await Client().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(before, _factory.Received.Count); // must not reach downstream unauthenticated
    }

    // 3. Trailing slash on an anon route: either matches anon (forward) or falls to catch-all (401). Both safe.
    [Fact]
    public async Task TrailingSlash_OnAnonRoute_IsSafe()
    {
        var before = _factory.Received.Count;
        var resp = await Client().SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login/"));
        var reachedStub = _factory.Received.Count > before;
        Assert.True(
            (reachedStub && resp.StatusCode == HttpStatusCode.OK) || resp.StatusCode == HttpStatusCode.Unauthorized,
            $"trailing-slash login produced status={resp.StatusCode}, reachedStub={reachedStub}");
    }

    // 4. Case variation on the anon path (YARP path match is case-insensitive): must stay safe.
    [Fact]
    public async Task CaseVariation_OnAnonRoute_IsSafe()
    {
        var before = _factory.Received.Count;
        var resp = await Client().SendAsync(new HttpRequestMessage(HttpMethod.Post, "/API/V1/AUTH/LOGIN"));
        var reachedStub = _factory.Received.Count > before;
        Assert.True(
            (reachedStub && resp.StatusCode == HttpStatusCode.OK) || resp.StatusCode == HttpStatusCode.Unauthorized,
            $"upper-cased login produced status={resp.StatusCode}, reachedStub={reachedStub}");
    }

    // 5. A protected path reached via a DIFFERENT casing must still require auth (no case-based bypass).
    [Fact]
    public async Task ProtectedPath_CaseVariation_StillRequiresAuth()
    {
        var before = _factory.Received.Count;
        var resp = await Client().GetAsync("/API/v1/BOOKINGS");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(before, _factory.Received.Count);
    }

    // 6. WhatsApp webhook with no JWT must still reach the stub (anon) with the Meta signature header preserved,
    //    proving strip/remint didn't break the genuinely-anonymous Meta inbound flow.
    [Fact]
    public async Task WhatsAppWebhook_NoToken_StillReachesStub_WithHubSignaturePreserved()
    {
        var before = _factory.Received.Count;
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/whatsapp/webhook");
        req.Headers.TryAddWithoutValidation("X-Hub-Signature-256", "sha256=deadbeef");
        req.Headers.TryAddWithoutValidation("Idempotency-Key", "abc-123");
        var resp = await Client().SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(_factory.Received.Count > before);
        var rec = _factory.LastRequest!;
        Assert.Equal("sha256=deadbeef", rec.Header("X-Hub-Signature-256")); // signature passed through unmodified
        Assert.Equal("abc-123", rec.Header("Idempotency-Key"));             // idempotency key passed through
    }

    // 7. Authorization + X-Purpose-Of-Use headers must pass through unmodified on an authed request.
    [Fact]
    public async Task PassThroughHeaders_AuthorizationAndPurposeOfUse_Preserved()
    {
        var token = _factory.MintUserToken(tenantId: Guid.NewGuid());
        var client = Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Purpose-Of-Use", "treatment");

        var resp = await client.GetAsync("/api/v1/bookings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var rec = _factory.LastRequest!;
        Assert.Equal("treatment", rec.Header("X-Purpose-Of-Use"));
        Assert.Equal($"Bearer {token}", rec.Header("Authorization"));
    }
}
