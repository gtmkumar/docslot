using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace mediq.ServiceDefaults;

/// <summary>
/// Builds a <see cref="ForwardedHeadersOptions"/> for X-Forwarded-For aware per-IP rate limiting, using a
/// STRICT default-deny trust model driven by the <c>ForwardedHeaders</c> config section.
/// <para>
/// Safe-by-default: the framework's <c>UseForwardedHeaders</c> ships with loopback (127.0.0.1/::1) trusted by
/// default. We CLEAR <see cref="ForwardedHeadersOptions.KnownIPNetworks"/> and
/// <see cref="ForwardedHeadersOptions.KnownProxies"/> first, then add ONLY explicitly configured entries. With
/// empty config the result is 0 proxies + 0 networks → <c>X-Forwarded-For</c> is IGNORED entirely → no spoofing
/// and the per-IP limiter keeps partitioning on the real socket address exactly as it does today.
/// </para>
/// <para>
/// Only <see cref="ForwardedHeaders.XForwardedFor"/> is honored (we do not consume the scheme, so
/// <c>X-Forwarded-Proto</c> is deliberately left out to keep the trust surface minimal). Invalid config entries
/// are skipped (TryParse) so a config typo can never crash boot; skipped entries are logged at Warning.
/// </para>
/// A deployment behind a load balancer / reverse proxy opts in by populating <c>ForwardedHeaders:KnownProxies</c>
/// (proxy IPs) and/or <c>ForwardedHeaders:KnownNetworks</c> (CIDR ranges) with the real intermediary addresses.
/// </summary>
public static class ForwardedHeadersConfig
{
    /// <summary>
    /// Reads the <c>ForwardedHeaders</c> section and returns a default-deny <see cref="ForwardedHeadersOptions"/>.
    /// Empty/absent config → 0 known proxies + 0 known networks (XFF ignored).
    /// </summary>
    public static ForwardedHeadersOptions Build(IConfiguration config, ILogger? logger = null)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardLimit = config.GetValue("ForwardedHeaders:ForwardLimit", 1),
        };

        // STRICT default-deny: drop the framework's default loopback trust so ONLY configured entries count.
        // On .NET 10 the live, framework-read collection is KnownIPNetworks (typed to System.Net.IPNetwork); the
        // legacy KnownNetworks (Microsoft.AspNetCore.HttpOverrides.IPNetwork) is deprecated (ASPDEPR005) and is
        // NOT what UseForwardedHeaders consults — so we clear + populate KnownIPNetworks, not KnownNetworks.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        foreach (var ip in config.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
        {
            if (IPAddress.TryParse(ip, out var addr))
                options.KnownProxies.Add(addr);
            else
                logger?.LogWarning("ForwardedHeaders:KnownProxies entry '{Entry}' is not a valid IP — skipped.", ip);
        }

        foreach (var cidr in config.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
        {
            // System.Net.IPNetwork (.NET 8+) parses CIDR (e.g. "10.0.0.0/8"). Fully qualified to disambiguate
            // from Microsoft.AspNetCore.HttpOverrides.IPNetwork (CS0104), which is in scope via the using above.
            if (System.Net.IPNetwork.TryParse(cidr, out var net))
                options.KnownIPNetworks.Add(net);
            else
                logger?.LogWarning("ForwardedHeaders:KnownNetworks entry '{Entry}' is not a valid CIDR — skipped.", cidr);
        }

        return options;
    }
}
