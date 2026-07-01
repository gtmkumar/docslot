using mediq.Application.Abstractions;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Offline/default <see cref="IGeoIpResolver"/> (issue #94). Performs NO external lookup — returns <c>null</c>
/// (unknown city) for every IP, so the security surfaces run with zero external credentials and the UI shows
/// just the raw IP. A real provider (MaxMind GeoLite2 / ip-api) replaces this only when configured.
/// </summary>
public sealed class NullGeoIpResolver : IGeoIpResolver
{
    public Task<string?> ResolveCityAsync(string? ipAddress, CancellationToken ct) => Task.FromResult<string?>(null);
}
