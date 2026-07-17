namespace mediq.SharedDataModel.Docslot.Geo;

/// <summary>An Indian PIN code resolved to its postal geography. <c>Areas</c> are the post-office
/// localities inside the code (e.g. "Purnia City", "Line Bazar"); <c>Latitude</c>/<c>Longitude</c>
/// are the code's approximate centroid and are null when the geocoder doesn't know the code —
/// a lookup is still useful without coordinates.</summary>
public sealed record PincodeLookupResult(
    string PinCode, string State, string District, IReadOnlyList<string> Areas,
    decimal? Latitude, decimal? Longitude);
