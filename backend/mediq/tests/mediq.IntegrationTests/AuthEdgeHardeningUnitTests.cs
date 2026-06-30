using System.Security.Cryptography;
using mediq.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace mediq.IntegrationTests;

/// <summary>
/// Plain xUnit unit tests (NO host) for the two Phase-4 auth-edge hardenings, exercised through their shared
/// statics in <c>mediq.ServiceDefaults</c>:
/// <list type="bullet">
/// <item><see cref="JwtSigningKeyGuard.Validate"/> — the fail-closed prod guard predicate.</item>
/// <item><see cref="ForwardedHeadersConfig.Build"/> — the STRICT default-deny X-Forwarded-For options builder.</item>
/// </list>
/// These are the primary coverage for the XFF builder: end-to-end XFF partitioning is NOT integration-testable
/// here (WebApplicationFactory's TestServer synthesizes RemoteIpAddress=127.0.0.1 and bypasses ForwardedHeaders),
/// so the builder's default-deny + parsing behavior is proven directly instead.
/// </summary>
public sealed class AuthEdgeHardeningUnitTests
{
    private const string DevKey = JwtSigningKeyGuard.DevSigningKeyBase64;

    /// <summary>Minimal IHostEnvironment fake — only EnvironmentName matters to the guard (IsDevelopment()).</summary>
    private sealed class FakeEnv(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    /// <summary>A genuinely strong, NON-dev 32-byte (256-bit) key, base64-encoded.</summary>
    private static string StrongKeyBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    // ---------------------------------------------------------------------------------------------------------
    //  (A) JwtSigningKeyGuard
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Guard_DevKey_OutsideDevelopment_NoAllowOverride_Throws()
    {
        var cfg = Config(("Jwt:SigningKey", DevKey));
        var ex = Assert.Throws<InvalidOperationException>(
            () => JwtSigningKeyGuard.Validate(cfg, new FakeEnv("Production")));
        Assert.Contains("refusing to start", ex.Message);
    }

    [Fact]
    public void Guard_DevKey_InDevelopment_DoesNotThrow()
    {
        // allowDev defaults to env.IsDevelopment() == true → dev key is accepted (warns only).
        var cfg = Config(("Jwt:SigningKey", DevKey));
        JwtSigningKeyGuard.Validate(cfg, new FakeEnv("Development"));
    }

    [Fact]
    public void Guard_DevKey_NonDev_WithAllowDevSigningKeyTrue_DoesNotThrow()
    {
        // Escape hatch: an explicit Jwt:AllowDevSigningKey=true overrides the env default (this is exactly how
        // the whole integration suite is exempted via TestHostConfig).
        var cfg = Config(("Jwt:SigningKey", DevKey), ("Jwt:AllowDevSigningKey", "true"));
        JwtSigningKeyGuard.Validate(cfg, new FakeEnv("Production"));
    }

    [Fact]
    public void Guard_ShortKey_OutsideDevelopment_Throws()
    {
        // 16 random bytes = 128-bit < the 256-bit minimum; and NOT the dev key, so the length gate is what bites.
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var ex = Assert.Throws<InvalidOperationException>(
            () => JwtSigningKeyGuard.Validate(Config(("Jwt:SigningKey", shortKey)), new FakeEnv("Production")));
        Assert.Contains("256-bit minimum", ex.Message);
    }

    [Fact]
    public void Guard_StrongNonDevKey_OutsideDevelopment_DoesNotThrow()
    {
        JwtSigningKeyGuard.Validate(Config(("Jwt:SigningKey", StrongKeyBase64())), new FakeEnv("Production"));
    }

    [Fact]
    public void Guard_MissingKey_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => JwtSigningKeyGuard.Validate(Config(), new FakeEnv("Production")));
        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public void Guard_NonBase64Key_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => JwtSigningKeyGuard.Validate(Config(("Jwt:SigningKey", "!!!not-base64!!!")), new FakeEnv("Production")));
        Assert.Contains("not valid base64", ex.Message);
    }

    [Fact]
    public void Guard_DevKey_DecodesAboveLengthFloor_SoBlocklistEqualityIsWhatCatchesIt()
    {
        // Defensive assertion of the spec's correctness note: the dev key is 51 bytes (>= 32), so the length
        // gate alone would PASS it — the base64 equality blocklist is the reason it's rejected in prod.
        Assert.True(Convert.FromBase64String(DevKey).Length >= 32);
    }

    // ---------------------------------------------------------------------------------------------------------
    //  (B) ForwardedHeadersConfig (STRICT default-deny)
    //  NOTE on the property: on .NET 10 the live, framework-read collection is KnownIPNetworks (System.Net.
    //  IPNetwork); the legacy KnownNetworks (deprecated, ASPDEPR005) is NOT what UseForwardedHeaders consults.
    //  The builder clears+populates KnownIPNetworks, so the assertions target KnownIPNetworks.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Xff_EmptyConfig_IsStrictDefaultDeny_ZeroProxies_ZeroNetworks()
    {
        var options = ForwardedHeadersConfig.Build(Config());

        // The framework default trusts loopback; a non-cleared options would have one KnownIPNetworks entry.
        // Empty on BOTH proves the default loopback trust was cleared and nothing was added → XFF is ignored.
        Assert.Empty(options.KnownProxies);
        Assert.Empty(options.KnownIPNetworks);
    }

    [Fact]
    public void Xff_ConfiguredProxiesAndNetworks_AreParsed()
    {
        var cfg = Config(
            ("ForwardedHeaders:KnownProxies:0", "10.1.2.3"),
            ("ForwardedHeaders:KnownProxies:1", "192.168.0.1"),
            ("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/8"),
            ("ForwardedHeaders:KnownNetworks:1", "172.16.0.0/12"));

        var options = ForwardedHeadersConfig.Build(cfg);

        Assert.Equal(2, options.KnownProxies.Count);
        Assert.Equal(2, options.KnownIPNetworks.Count);
    }

    [Fact]
    public void Xff_InvalidEntries_AreSkipped_NotThrown()
    {
        var cfg = Config(
            ("ForwardedHeaders:KnownProxies:0", "10.1.2.3"),       // valid
            ("ForwardedHeaders:KnownProxies:1", "not-an-ip"),      // skipped
            ("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/8"),    // valid
            ("ForwardedHeaders:KnownNetworks:1", "garbage/cidr"),  // skipped
            ("ForwardedHeaders:KnownNetworks:2", "999.0.0.0/8"));  // skipped (bad octet)

        var options = ForwardedHeadersConfig.Build(cfg);

        // A config typo must never crash boot — only the valid entries survive.
        Assert.Single(options.KnownProxies);
        Assert.Single(options.KnownIPNetworks);
    }

    [Fact]
    public void Xff_ForwardLimit_DefaultsToOne()
    {
        Assert.Equal(1, ForwardedHeadersConfig.Build(Config()).ForwardLimit);
    }
}
