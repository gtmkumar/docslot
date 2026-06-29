using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace mediq.IntegrationTests;

/// <summary>
/// Proves the YARP edge gateway is an ENFORCED trust boundary: only the verified anonymous allow-list reaches
/// the downstream without a token; everything else under <c>/api/v1/**</c> is rejected at the edge (the stub
/// downstream never sees it). Also verifies the header sanitization (X-Tenant-Id strip, X-Correlation-ID remint)
/// and the security response headers. The rate-limit test is isolated on its own factory so its 127.0.0.1
/// bucket can't poison the shared cases. In the "Gateway" collection so it serializes against the other
/// gateway class (see <see cref="GatewayCollection"/>) instead of running its host concurrently.
/// </summary>
[Collection("Gateway")]
public sealed class GatewayTrustBoundaryTests(GatewayWebAppFactory factory) : IClassFixture<GatewayWebAppFactory>
{
    private readonly GatewayWebAppFactory _factory = factory;

    // CreateClient with redirects OFF — the /ref route would 302, and we assert on the edge response, not the
    // redirect target. Also keeps every request a single round-trip for clean rate-limit accounting elsewhere.
    private HttpClient Client() =>
        _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

    // ---- (a) every anonymous allow-list route reaches the stub WITHOUT a token ---------------------------

    public static TheoryData<string, HttpMethod, string> AnonymousRoutes() => new()
    {
        { "/api/v1/auth/login",        HttpMethod.Post, "auth-login" },
        { "/api/v1/auth/refresh",      HttpMethod.Post, "auth-refresh" },
        { "/api/v1/oauth/token",       HttpMethod.Post, "oauth-token" },
        { "/api/v1/oauth/revoke",      HttpMethod.Post, "oauth-revoke" },
        { "/api/v1/whatsapp/webhook",  HttpMethod.Get,  "whatsapp-webhook-get" },
        { "/api/v1/whatsapp/webhook",  HttpMethod.Post, "whatsapp-webhook-post" },
        { "/api/v1/ref/abc123",        HttpMethod.Get,  "referral-shortcode" },
    };

    [Theory]
    [MemberData(nameof(AnonymousRoutes))]
    public async Task AnonRoute_WithoutToken_ReachesStub(string path, HttpMethod method, string _)
    {
        var before = _factory.Received.Count;
        var resp = await Client().SendAsync(new HttpRequestMessage(method, path));

        // Not rejected at the edge — it proxied through to the stub (which returns 200 "stub-ok").
        Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(_factory.Received.Count > before, "stub downstream should have received the anon request");
    }

    // ---- (b) a non-allow-listed path with NO token → 401 at the edge AND the stub never receives it -------

    [Fact]
    public async Task NonAllowListed_WithoutToken_Rejected401_AndStubNeverReceives()
    {
        var before = _factory.Received.Count;
        // /api/v1/public/* uses client-credential tokens — still requires a JWT past the boundary.
        var resp = await Client().GetAsync("/api/v1/public/something");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(before, _factory.Received.Count); // edge rejected it; downstream untouched
    }

    [Fact]
    public async Task ArbitraryProtectedPath_WithoutToken_Rejected401_AndStubNeverReceives()
    {
        var before = _factory.Received.Count;
        var resp = await Client().GetAsync("/api/v1/bookings");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(before, _factory.Received.Count);
    }

    // ---- (c) a valid USER JWT proxies through ------------------------------------------------------------

    [Fact]
    public async Task ValidUserToken_ProxiesThrough()
    {
        var before = _factory.Received.Count;
        var client = Client();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.MintUserToken(tenantId: Guid.NewGuid()));

        var resp = await client.GetAsync("/api/v1/bookings");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(_factory.Received.Count > before);
    }

    // ---- (d) a valid CLIENT-shaped JWT (same issuer/audience/key) proxies through ------------------------

    [Fact]
    public async Task ValidClientToken_ProxiesThrough()
    {
        var before = _factory.Received.Count;
        var client = Client();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.MintClientToken());

        // /api/v1/public/* is the client-credential surface; the edge only checks authentication, not scope.
        var resp = await client.GetAsync("/api/v1/public/bookings");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(_factory.Received.Count > before);
    }

    // ---- (e) invalid / expired / wrong-issuer tokens → 401 at the edge -----------------------------------

    [Fact]
    public async Task GarbageToken_Rejected401()
    {
        var client = Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-jwt");
        var resp = await client.GetAsync("/api/v1/bookings");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_Rejected401_AndStubNeverReceives()
    {
        var before = _factory.Received.Count;
        var client = Client();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.MintExpiredToken());
        var resp = await client.GetAsync("/api/v1/bookings");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(before, _factory.Received.Count);
    }

    [Fact]
    public async Task WrongIssuerToken_Rejected401()
    {
        var client = Client();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.MintUserToken(issuer: "evil-issuer"));
        var resp = await client.GetAsync("/api/v1/bookings");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---- (f) X-Tenant-Id is stripped at the edge ---------------------------------------------------------

    [Fact]
    public async Task XTenantId_IsStrippedBeforeReachingDownstream()
    {
        var client = Client();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.MintUserToken(tenantId: Guid.NewGuid()));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Guid.NewGuid().ToString());

        var resp = await client.GetAsync("/api/v1/bookings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var received = _factory.LastRequest!;
        Assert.False(received.HasHeader("X-Tenant-Id"), "gateway must strip X-Tenant-Id");
    }

    // ---- (g) a client-supplied X-Correlation-ID is reminted (downstream sees a DIFFERENT value) ----------

    [Fact]
    public async Task ClientSuppliedCorrelationId_IsReminted()
    {
        var forged = Guid.NewGuid().ToString();
        var client = Client();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.MintUserToken());
        client.DefaultRequestHeaders.Add("X-Correlation-ID", forged);

        var resp = await client.GetAsync("/api/v1/bookings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var downstreamSaw = _factory.LastRequest!.Header("X-Correlation-ID");
        Assert.NotNull(downstreamSaw);
        Assert.NotEqual(forged, downstreamSaw); // forged id was discarded, gateway minted a fresh one
        Assert.True(Guid.TryParse(downstreamSaw, out _), "reminted id should be a GUID");
    }

    // ---- (h) absent X-Correlation-ID → downstream sees a generated one (echoed back) ---------------------

    [Fact]
    public async Task AbsentCorrelationId_IsGenerated()
    {
        var client = Client();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.MintUserToken());

        var resp = await client.GetAsync("/api/v1/bookings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var downstreamSaw = _factory.LastRequest!.Header("X-Correlation-ID");
        Assert.NotNull(downstreamSaw);
        Assert.True(Guid.TryParse(downstreamSaw, out _));
        // stub echoes the forwarded id back on the response — confirms what crossed the boundary.
        Assert.Equal(downstreamSaw, resp.Headers.GetValues("X-Correlation-ID").Single());
    }

    // ---- (j) security headers: nosniff globally + no-store on auth/token responses -----------------------

    [Fact]
    public async Task NosniffHeader_PresentOnEveryResponse()
    {
        // anonymous route (no token) — header must still be applied
        var resp = await Client().PostAsync("/api/v1/auth/login", JsonContent.Create(new { }));
        Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Theory]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/v1/auth/refresh")]
    [InlineData("/api/v1/oauth/token")]
    public async Task NoStoreCacheControl_OnTokenResponses(string path)
    {
        var resp = await Client().PostAsync(path, JsonContent.Create(new { }));
        var cacheControl = resp.Headers.CacheControl;
        Assert.NotNull(cacheControl);
        Assert.True(cacheControl!.NoStore, $"{path} response must carry Cache-Control: no-store");
    }

    [Fact]
    public async Task NoStore_IsNotForcedOnReferralRedirect()
    {
        // The /ref 302 is deliberately cacheable — assert no-store is NOT slapped on it.
        var resp = await Client().GetAsync("/api/v1/ref/abc123");
        Assert.NotEqual(true, resp.Headers.CacheControl?.NoStore);
    }
}

/// <summary>
/// (i) Rate-limit isolation. The edge limiter partitions on the SOCKET ip (127.0.0.1 in tests); a fresh factory
/// gives this test its OWN gateway process with its OWN limiter bucket, so firing past the 200/min window here
/// cannot poison the shared <see cref="GatewayTrustBoundaryTests"/> cases (and vice-versa). NOT an IClassFixture
/// — this class owns and disposes its factory. In the "Gateway" collection so its 200-request burst runs
/// sequentially with the other gateway class rather than concurrently piling host/CPU pressure on the suite.
/// </summary>
[Collection("Gateway")]
public sealed class GatewayRateLimitTests : IAsyncLifetime
{
    private GatewayWebAppFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new GatewayWebAppFactory();
        await _factory.InitializeAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task SingleIp_PastWindow_Gets429()
    {
        var client = _factory.CreateClient();
        // Edge limiter is 200/min per IP and runs BEFORE auth → use an anonymous route so 401 doesn't mask 429.
        // Fire past the window; the 201st+ within the minute should be rejected with 429.
        HttpStatusCode? sawTooMany = null;
        for (var i = 0; i < 230; i++)
        {
            var resp = await client.GetAsync("/api/v1/whatsapp/webhook");
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                sawTooMany = resp.StatusCode;
                break;
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, sawTooMany);
    }
}
