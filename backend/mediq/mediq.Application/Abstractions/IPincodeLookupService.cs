using mediq.SharedDataModel.Docslot.Geo;

namespace mediq.Application.Abstractions;

/// <summary>Resolves an Indian PIN code to state/district/areas + approximate coordinates.
/// Implementations call external reference services (India Post directory, OSM geocoder) and are
/// expected to cache aggressively — postal geography is effectively immutable. Returns null for an
/// unknown code; coordinate fields are null when only the postal directory (no geocoder) answered.</summary>
public interface IPincodeLookupService
{
    Task<PincodeLookupResult?> LookupAsync(string pinCode, CancellationToken ct);
}
