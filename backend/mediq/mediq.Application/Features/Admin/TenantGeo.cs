using System.Text.Json;

namespace mediq.Application.Features.Admin;

/// <summary>Reads the geo centroid stored under <c>tenants.settings.geo</c> (written by CreateAsync/UpdateAsync as
/// <c>{ "geo": { "latitude": &lt;num&gt;, "longitude": &lt;num&gt;, "source": "pincode_lookup", "tagged_at": ... } }</c>).
/// The tenant detail projections use this so the edit form pre-fills the current coordinates. Fail-soft: any
/// missing/malformed geo yields <c>(null, null)</c> — a tenant may simply have no tag.</summary>
internal static class TenantGeo
{
    public static (decimal? Latitude, decimal? Longitude) Read(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("geo", out var geo)
                && geo.ValueKind == JsonValueKind.Object
                && geo.TryGetProperty("latitude", out var lat) && lat.ValueKind == JsonValueKind.Number
                && geo.TryGetProperty("longitude", out var lng) && lng.ValueKind == JsonValueKind.Number
                && lat.TryGetDecimal(out var latValue) && lng.TryGetDecimal(out var lngValue))
                return (latValue, lngValue);
        }
        catch (JsonException)
        {
            // Malformed settings blob — treat as untagged rather than failing the read.
        }
        return (null, null);
    }
}
