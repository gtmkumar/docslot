namespace mediq.Application.Abstractions;

/// <summary>
/// Resolves an IP address to a coarse city label for security surfaces (audit-log rows #86, active sessions
/// #87). Sits behind an OFFLINE PROVIDER SEAM (issue #94, epic #80 Phase D): the dev/default
/// <c>NullGeoIpResolver</c> performs NO external lookup and returns <c>null</c> (unknown) for every IP, so the
/// stack runs with zero external credentials and the UI simply shows the raw IP. A real geo provider
/// (e.g. MaxMind GeoLite2 DB or an ip-api.com key) is wired ONLY when configured — never on the default path.
/// City is a display hint, never PHI and never a tenant-isolation guard.
/// </summary>
public interface IGeoIpResolver
{
    /// <summary>
    /// Best-effort city for <paramref name="ipAddress"/>, or <c>null</c> when unknown / not configured / the IP
    /// is null-or-empty. Implementations must not throw for an unresolvable IP — an unknown city is not an error.
    /// </summary>
    Task<string?> ResolveCityAsync(string? ipAddress, CancellationToken ct);
}
