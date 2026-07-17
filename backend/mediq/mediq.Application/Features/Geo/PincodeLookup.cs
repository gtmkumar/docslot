using System.Text.RegularExpressions;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Geo;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Geo;

// ============================================================================
// PIN-code reference lookup — powers address auto-fill (state/city from PIN)
// and clinic geo-tagging on the onboarding form. Read-only reference data:
// no tenant scope, no PHI, cacheable. Unknown codes are a 404, not an error.
// ============================================================================

/// <summary>Resolve an Indian PIN code to state/district/areas (+ centroid coordinates when the
/// geocoder knows the code). Any authenticated user; gated at the controller by auth only.</summary>
public sealed partial record PincodeLookupQuery(string PinCode) : IQuery<PincodeLookupResult>;

public sealed partial class PincodeLookupQueryHandler(IPincodeLookupService pincodes)
    : IQueryHandler<PincodeLookupQuery, PincodeLookupResult>
{
    [GeneratedRegex("^[1-9][0-9]{5}$")]
    private static partial Regex PinShape();

    public async Task<PincodeLookupResult> Handle(PincodeLookupQuery query, CancellationToken ct)
    {
        var pin = query.PinCode.Trim();
        if (!PinShape().IsMatch(pin))
            throw new BusinessRuleException("An Indian PIN code is 6 digits and cannot start with 0.");

        return await pincodes.LookupAsync(pin, ct)
            ?? throw new KeyNotFoundException($"PIN code {pin} was not found in the postal directory.");
    }
}
