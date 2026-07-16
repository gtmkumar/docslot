using System.Globalization;
using System.Text.Json;
using mediq.Application.Abstractions;
using mediq.SharedDataModel.Docslot.Geo;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace mediq.Infrastructure.Geo;

/// <summary>
/// PIN-code resolution against two public reference services:
///   1. India Post postal directory (api.postalpincode.in) — authoritative for state/district and
///      the post-office localities inside a code. This answer decides found vs 404.
///   2. OSM Nominatim — best-effort centroid coordinates for the geo tag. Failures here NEVER fail
///      the lookup (a clinic can be onboarded without coordinates); they just leave lat/long null.
/// Postal geography is effectively immutable, so successful lookups cache for 24h and unknown codes
/// for 10 minutes (typo retries shouldn't hammer the upstream). Nominatim's usage policy requires an
/// identifying User-Agent + low volume — both satisfied here (admin-form frequency, cached).
/// </summary>
public sealed class PincodeLookupService(
    IHttpClientFactory httpFactory, IMemoryCache cache, ILogger<PincodeLookupService> logger)
    : IPincodeLookupService
{
    public const string IndiaPostClient = "geo-indiapost";
    public const string NominatimClient = "geo-nominatim";

    private static readonly TimeSpan HitTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(10);

    public async Task<PincodeLookupResult?> LookupAsync(string pinCode, CancellationToken ct)
    {
        var cacheKey = $"geo:pincode:{pinCode}";
        if (cache.TryGetValue(cacheKey, out PincodeLookupResult? cached)) return cached;

        var postal = await QueryIndiaPostAsync(pinCode, ct);
        if (postal is null)
        {
            cache.Set(cacheKey, (PincodeLookupResult?)null, MissTtl);
            return null;
        }

        var (lat, lon) = await TryQueryNominatimAsync(pinCode, ct);
        var result = postal with { Latitude = lat, Longitude = lon };
        cache.Set(cacheKey, (PincodeLookupResult?)result, HitTtl);
        return result;
    }

    /// <summary>India Post directory. Response shape: [{ Status, PostOffice: [{ Name, District, State }] }].</summary>
    private async Task<PincodeLookupResult?> QueryIndiaPostAsync(string pinCode, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient(IndiaPostClient);
            using var resp = await http.GetAsync($"pincode/{pinCode}", ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var first = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                ? doc.RootElement[0]
                : default;
            if (first.ValueKind != JsonValueKind.Object) return null;
            if (!first.TryGetProperty("PostOffice", out var offices) || offices.ValueKind != JsonValueKind.Array ||
                offices.GetArrayLength() == 0)
                return null;

            var head = offices[0];
            var state = head.TryGetProperty("State", out var s) ? s.GetString() : null;
            var district = head.TryGetProperty("District", out var d) ? d.GetString() : null;
            if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(district)) return null;

            var areas = offices.EnumerateArray()
                .Select(o => o.TryGetProperty("Name", out var n) ? n.GetString() : null)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new PincodeLookupResult(pinCode, state, district, areas, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Upstream flake → behave like "not found" (the form's free-entry path still works).
            logger.LogWarning(ex, "India Post lookup failed for PIN {PinCode}", pinCode);
            return null;
        }
    }

    /// <summary>Best-effort centroid from Nominatim; never fails the lookup.</summary>
    private async Task<(decimal? Lat, decimal? Lon)> TryQueryNominatimAsync(string pinCode, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient(NominatimClient);
            using var resp = await http.GetAsync(
                $"search?postalcode={pinCode}&countrycodes=in&format=jsonv2&limit=1", ct);
            if (!resp.IsSuccessStatusCode) return (null, null);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return (null, null);

            var hit = doc.RootElement[0];
            decimal? lat = null, lon = null;
            if (hit.TryGetProperty("lat", out var latEl) &&
                decimal.TryParse(latEl.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var latV))
                lat = latV;
            if (hit.TryGetProperty("lon", out var lonEl) &&
                decimal.TryParse(lonEl.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var lonV))
                lon = lonV;
            return lat is not null && lon is not null ? (lat, lon) : (null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Nominatim geocode failed for PIN {PinCode}", pinCode);
            return (null, null);
        }
    }
}
