using mediq.Api.Authorization;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Docslot.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// Tenant Settings surface (<c>docslot.healthcare_facilities</c>, one row per tenant). READ is gated by
/// <c>tenant.settings.read</c>, the PATCH by <c>tenant.settings.update</c> (tenant_owner holds both). tenant_id
/// always comes from the JWT (never a header). The whatsapp_access_token secret is never returned, and the
/// PATCH refuses anything but businessHours / appointmentSettings (those are not part of the request shape).
/// </summary>
[ApiController]
[Route("api/v1/settings")]
[Authorize]
public sealed class SettingsController(
    ICommandDispatcher commands, IQueryDispatcher queries, ICurrentUserContext currentUser) : ControllerBase
{
    /// <summary>Read the caller's tenant facility settings. 404 when no facility row exists for the tenant.</summary>
    [HttpGet]
    [RequirePermission("tenant.settings.read")]
    [ProducesResponseType<SettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SettingsDto>> Get(CancellationToken ct)
        => Ok(await queries.Query(new GetSettingsQuery(RequireTenant()), ct));

    /// <summary>
    /// Partially update businessHours and/or appointmentSettings for the caller's tenant facility. Runs inside
    /// the tenant-scoped UnitOfWork transaction; returns the updated <see cref="SettingsDto"/>. 404 when no
    /// facility row exists. A PATCH here is not a money/booking mutation, so an Idempotency-Key is not required.
    /// </summary>
    [HttpPatch]
    [RequirePermission("tenant.settings.update")]
    [ProducesResponseType<SettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SettingsDto>> Update([FromBody] UpdateSettingsRequest request, CancellationToken ct)
        => Ok(await commands.Send(new UpdateSettingsCommand(RequireTenant(), request), ct));

    private Guid RequireTenant() =>
        currentUser.TenantId ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");
}
