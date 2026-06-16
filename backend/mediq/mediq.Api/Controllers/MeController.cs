using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Me;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// The authenticated caller's own profile, permissions, menus, and badges. Every endpoint here is gated
/// only by authentication — the content is already scoped to the caller server-side. Permissions/menus
/// come from the resolve-once-per-request set (the frontend never branches on role).
/// </summary>
[ApiController]
[Route("api/v1/me")]
[Authorize]
public sealed class MeController(
    IQueryDispatcher queries, ICurrentUserContext currentUser, ITenantRepository tenants) : ControllerBase
{
    /// <summary>GET /api/v1/me — profile + tenant memberships.</summary>
    [HttpGet]
    [ProducesResponseType<MeDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MeDto>> Me(CancellationToken ct)
        => Ok(await queries.Query(new GetMeQuery(RequireUserId(), currentUser.TenantId), ct));

    /// <summary>GET /api/v1/me/permissions — the flat effective permission key set (resolve-once).</summary>
    [HttpGet("permissions")]
    [ProducesResponseType<PermissionSetDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PermissionSetDto>> Permissions(CancellationToken ct)
        => Ok(await queries.Query(new GetMyPermissionsQuery(RequireUserId(), currentUser.TenantId), ct));

    /// <summary>GET /api/v1/me/menus — backend-driven bilingual menu tree, tenant-type-aware.</summary>
    [HttpGet("menus")]
    [ProducesResponseType<IReadOnlyList<MenuNodeDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MenuNodeDto>>> Menus(CancellationToken ct)
    {
        var tenantId = currentUser.TenantId
            ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");

        var tenant = await tenants.GetByIdAsync(tenantId, ct);
        var menus = await queries.Query(
            new GetMyMenusQuery(RequireUserId(), tenantId, tenant?.TenantType), ct);
        return Ok(menus);
    }

    /// <summary>GET /api/v1/me/badges — counts keyed by navigation badge_source (stub until slice 03).</summary>
    [HttpGet("badges")]
    [ProducesResponseType<BadgesDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BadgesDto>> Badges(CancellationToken ct)
        => Ok(await queries.Query(new GetMyBadgesQuery(RequireUserId(), currentUser.TenantId), ct));

    private Guid RequireUserId() =>
        currentUser.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
}
