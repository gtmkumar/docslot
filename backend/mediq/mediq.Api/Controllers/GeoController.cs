using mediq.Application.Cqrs;
using mediq.Application.Features.Geo;
using mediq.SharedDataModel.Docslot.Geo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// Reference geography. Non-tenant, non-PHI lookup data (postal directory + geocode centroids),
/// so endpoints gate on authentication only — no permission key, no tenant scope.
/// </summary>
[ApiController]
[Route("api/v1/geo")]
[Authorize]
public sealed class GeoController(IQueryDispatcher queries) : ControllerBase
{
    /// <summary>Resolve an Indian PIN code → state, district, post-office areas, and (best-effort)
    /// centroid coordinates. 404 for a code the postal directory doesn't know; 422 for a malformed one.</summary>
    [HttpGet("pincode/{pinCode}")]
    [ProducesResponseType<PincodeLookupResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PincodeLookupResult>> LookupPincode(string pinCode, CancellationToken ct)
        => Ok(await queries.Query(new PincodeLookupQuery(pinCode), ct));
}
