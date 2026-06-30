using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace mediq.IntegrationTests;

/// <summary>
/// Integration coverage for the JWT prod-guard (<c>JwtSigningKeyGuard</c>) on the REAL edge gateway host.
/// <para>
/// Proves the guard's two ends on a booting host (not just the predicate unit test):
/// <list type="number">
/// <item>Production + the committed dev key + <c>Jwt:AllowDevSigningKey=false</c> → the host REFUSES to start
///   (the guard throws <see cref="InvalidOperationException"/> mentioning the dev key / "refusing to start").</item>
/// <item>Production + a STRONG non-dev key + <c>Jwt:AllowDevSigningKey=false</c> → the host boots and serves; an
///   anonymous route returns a non-401 (a good prod key is accepted).</item>
/// </list>
/// </para>
/// In the "Gateway" collection so its host spins up sequentially with the other gateway classes rather than
/// piling concurrent host/CPU pressure on the timing-sensitive DB-concurrency tests. Each test owns and disposes
/// its own factory (mirrors <c>GatewayRateLimitTests</c>) so the per-host config override can't leak.
/// <para>
/// NOTE: the suite-wide <c>TestHostConfig</c> sets the ENV VAR <c>Jwt__AllowDevSigningKey=true</c> to exempt
/// every other host. These factories override it back to <c>false</c> via <c>AddInMemoryCollection</c>, which is
/// layered AFTER the default <c>AddEnvironmentVariables()</c> provider, so the in-memory value wins for this host
/// only — restoring the guard so the prod throw can be asserted.
/// </para>
/// </summary>
[Collection("Gateway")]
public sealed class GatewayAuthEdgeTests
{
    /// <summary>
    /// A gateway factory pinned to Production with an explicit <c>Jwt:SigningKey</c> +
    /// <c>Jwt:AllowDevSigningKey=false</c>, so the prod-guard is in force. Reuses the base
    /// <see cref="GatewayWebAppFactory"/> (its stub downstream + service-discovery shadowing) and only adds the
    /// auth-edge config on top — the in-memory layer is applied LAST so it overrides both appsettings and the
    /// base factory's own in-memory entries.
    /// </summary>
    private sealed class ProdGuardGatewayFactory(string signingKey) : GatewayWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);               // stub-address shadowing etc. (sets Development)
            builder.UseEnvironment("Production");         // override: the guard only bites outside Development
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:SigningKey"] = signingKey,
                    ["Jwt:AllowDevSigningKey"] = "false", // beats the suite-wide env-var exemption for THIS host
                });
            });
        }
    }

    /// <summary>A genuinely strong, NON-dev 256-bit key, base64-encoded.</summary>
    private static string StrongKeyBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    // ---- (1) Production + dev key + AllowDevSigningKey=false → host refuses to start --------------------------

    [Fact]
    public async Task Production_WithCommittedDevKey_RefusesToStart()
    {
        await using var factory = new ProdGuardGatewayFactory(GatewayWebAppFactory.SigningKeyBase64);
        await factory.InitializeAsync();   // starts only the stub downstream; the gateway boots lazily below

        // The guard runs post-build during the host's start, which WebApplicationFactory triggers on first
        // client creation — so the InvalidOperationException surfaces HERE.
        var ex = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(ex);
        var ioe = Assert.IsType<InvalidOperationException>(Unwrap(ex!));
        Assert.Contains("refusing to start", ioe.Message);
        Assert.Contains("development JWT signing key", ioe.Message);
    }

    // ---- (2) Production + strong non-dev key → boots; an anonymous route returns non-401 ---------------------

    [Fact]
    public async Task Production_WithStrongNonDevKey_Boots_AndAnonRouteIsReachable()
    {
        await using var factory = new ProdGuardGatewayFactory(StrongKeyBase64());
        await factory.InitializeAsync();

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Anonymous allow-list route — no dev-key-minted token needed (the strong key changed the signing secret,
        // so the factory's dev-key tokens would no longer validate anyway). A good prod key boots → the edge
        // proxies the anon request to the stub (200), proving the guard did NOT block a legitimate key.
        var resp = await client.GetAsync("/api/v1/whatsapp/webhook");

        Assert.NotEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    /// <summary>
    /// WebApplicationFactory may surface a startup throw wrapped (e.g. TypeInitialization / aggregate). Peel to
    /// the InvalidOperationException the guard raised if it's nested.
    /// </summary>
    private static Exception Unwrap(Exception ex)
    {
        var current = ex;
        while (current is not InvalidOperationException && current.InnerException is not null)
            current = current.InnerException;
        return current;
    }
}
