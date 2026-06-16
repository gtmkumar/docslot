using mediq.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// The third-party-facing API surface consumed with a client-credentials token. Each endpoint is gated by
/// a <see cref="RequireScopeAttribute"/> — the client must hold the scope in its token (resolve-once,
/// in-memory). This is the scope-enforcement counterpart to the user-permission API. The actual data
/// (bookings/patients) arrives with slice 03; slice 02 ships the scoped seam + a probe endpoint.
/// </summary>
[ApiController]
[Route("api/v1/public")]
[Authorize]
public sealed class PublicApiController : ControllerBase
{
    /// <summary>GET /api/v1/public/bookings — requires the <c>docslot.bookings.read</c> scope.</summary>
    [HttpGet("bookings")]
    [RequireScope("docslot.bookings.read")]
    public IActionResult ListBookings()
        => Ok(new { ok = true, scope = "docslot.bookings.read", data = Array.Empty<object>() });

    /// <summary>GET /api/v1/public/patients — requires the (dangerous, consent-bound) <c>docslot.patients.read</c> scope.</summary>
    [HttpGet("patients")]
    [RequireScope("docslot.patients.read")]
    public IActionResult ListPatients()
        => Ok(new { ok = true, scope = "docslot.patients.read", data = Array.Empty<object>() });
}
