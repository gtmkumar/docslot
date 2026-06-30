extern alias Gateway;

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace mediq.IntegrationTests;

/// <summary>
/// Boots the REAL edge gateway (<c>Gateway::Program</c>, aliased so its global <c>Program</c> stays distinct
/// from mediq.Api's) via <see cref="WebApplicationFactory{TEntryPoint}"/> and points its single YARP cluster at
/// an in-process Kestrel <b>stub downstream</b> instead of the unresolvable service-discovery name
/// <c>https+http://mediq-api</c>.
/// <para>
/// The stub maps <c>GET/POST /api/v1/{**catch-all}</c>, records what YARP forwarded (path, method, headers) into
/// <see cref="LastRequest"/>, and echoes the received <c>X-Correlation-ID</c> back on the response so a test can
/// assert the gateway reminted it. The stub's bound port is captured at startup and injected into the gateway's
/// config via <c>AddInMemoryCollection</c>, shadowing the service-discovery destination so the concrete localhost
/// address wins.
/// </para>
/// JWT minting mirrors mediq.Infrastructure's JwtTokenService exactly (HMAC-SHA256 over the base64
/// <c>Jwt:SigningKey</c>, issuer <c>docslot-platform</c>, audience <c>docslot-clients</c>) so the gateway's
/// existing, UNCHANGED edge validation accepts the test tokens — for both the user and the client token shape.
/// </summary>
// Not sealed: GatewayAuthEdgeTests derives a Production-pinned variant to exercise the JWT prod-guard while
// reusing this factory's stub downstream + service-discovery shadowing.
public class GatewayWebAppFactory : WebApplicationFactory<Gateway::Program>, IAsyncLifetime
{
    // Must match mediq.Gateway/appsettings.json Jwt section (the edge validation config is left untouched).
    public const string Issuer = "docslot-platform";
    public const string Audience = "docslot-clients";
    public const string SigningKeyBase64 = "ZGV2LW9ubHktc2lnbmluZy1rZXktcmVwbGFjZS1pbi1wcm9kdWN0aW9uLTI1Ni1iaXQh";

    public const string TenantClaim = "tenant_id";
    public const string TokenUseClaim = "token_use";
    public const string ClientIdClaim = "client_id";
    public const string ScopeClaim = "scope";

    /// <summary>Records every request the stub downstream received (most-recent-last).</summary>
    public ConcurrentQueue<RecordedRequest> Received { get; } = new();

    public RecordedRequest? LastRequest => Received.TryPeekLast(out var r) ? r : null;

    public sealed record RecordedRequest(string Path, string Method, IReadOnlyDictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        public bool HasHeader(string name) => Headers.ContainsKey(name);
    }

    private WebApplication? _stub;
    private string _stubAddress = "http://127.0.0.1:0";

    /// <summary>Spins up the stub downstream first so we know its port before the gateway host configures.</summary>
    public async Task InitializeAsync()
    {
        var stubBuilder = WebApplication.CreateBuilder();
        stubBuilder.WebHost.UseUrls("http://127.0.0.1:0");          // dynamic port — avoids cross-test clashes
        stubBuilder.Logging.ClearProviders();
        _stub = stubBuilder.Build();

        _stub.Map("/api/v1/{**catch-all}", async (HttpContext ctx) =>
        {
            var headers = ctx.Request.Headers
                .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            Received.Enqueue(new RecordedRequest(ctx.Request.Path, ctx.Request.Method, headers));

            // Echo the correlation id YARP forwarded so a test can compare it against what the client sent.
            if (headers.TryGetValue("X-Correlation-ID", out var corr))
                ctx.Response.Headers["X-Correlation-ID"] = corr;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.WriteAsync("stub-ok");
        });

        await _stub.StartAsync();

        var server = _stub.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _stubAddress = address;   // e.g. http://127.0.0.1:53421
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            // Shadow the service-discovery destination (https+http://mediq-api), which can't resolve in tests,
            // with the concrete stub address. Last provider wins, so this overrides appsettings.json.
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:Clusters:platform-api-cluster:Destinations:primary:Address"] = _stubAddress,
            });
        });
    }

    public new async Task DisposeAsync()
    {
        if (_stub is not null)
        {
            await _stub.StopAsync();
            await _stub.DisposeAsync();
        }
        await base.DisposeAsync();
    }

    /// <summary>Mints a user-login-shaped access token (sub/email/jti + optional tenant_id).</summary>
    public string MintUserToken(Guid? userId = null, Guid? tenantId = null, TimeSpan? lifetime = null, string? issuer = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, (userId ?? Guid.NewGuid()).ToString()),
            new(JwtRegisteredClaimNames.Email, "edge.user@docslot.test"),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
        };
        if (tenantId is { } tid)
            claims.Add(new Claim(TenantClaim, tid.ToString()));
        return Mint(claims, lifetime ?? TimeSpan.FromMinutes(15), issuer ?? Issuer);
    }

    /// <summary>Mints a client-credentials-shaped token (token_use=client, client_id, scope) — minted IDENTICALLY
    /// to user tokens (same issuer/audience/key), so the gateway's single edge config accepts it too.</summary>
    public string MintClientToken(Guid? clientId = null, string scope = "docslot.bookings.read")
    {
        var id = clientId ?? Guid.NewGuid();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, id.ToString()),
            new(ClientIdClaim, id.ToString()),
            new(TokenUseClaim, "client"),
            new(ScopeClaim, scope),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
        };
        return Mint(claims, TimeSpan.FromMinutes(15), Issuer);
    }

    /// <summary>Mints an already-expired token to prove edge lifetime validation rejects it.</summary>
    public string MintExpiredToken()
    {
        var key = new SymmetricSecurityKey(Convert.FromBase64String(SigningKeyBase64));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
        };
        // notBefore + expiry both safely in the past (clearing the 30s clock skew the gateway allows).
        var token = new JwtSecurityToken(Issuer, Audience, claims,
            notBefore: DateTime.UtcNow.AddMinutes(-10), expires: DateTime.UtcNow.AddMinutes(-5), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string Mint(IEnumerable<Claim> claims, TimeSpan lifetime, string issuer)
    {
        var key = new SymmetricSecurityKey(Convert.FromBase64String(SigningKeyBase64));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(issuer, Audience, claims,
            notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.Add(lifetime), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

internal static class ConcurrentQueueExtensions
{
    /// <summary>Peeks the most-recently-enqueued item (ConcurrentQueue only exposes the head natively).</summary>
    public static bool TryPeekLast<T>(this ConcurrentQueue<T> queue, out T last)
    {
        last = default!;
        var found = false;
        foreach (var item in queue) { last = item; found = true; }
        return found;
    }
}
