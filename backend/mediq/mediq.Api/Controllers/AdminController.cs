using mediq.Api.Authorization;
using mediq.Application.Cqrs;
using mediq.Application.Features.Admin;
using mediq.SharedDataModel.Docslot.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// platform_core admin surface: tenants, users, roles, role assignments, permission overrides. Every
/// endpoint is gated by an in-memory <see cref="RequirePermissionAttribute"/> check against the
/// resolve-once permission set — canonical permission keys, never role names.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class AdminController(ICommandDispatcher commands, IQueryDispatcher queries) : ControllerBase
{
    // ---- Tenants -------------------------------------------------------------------------------

    [HttpGet("tenants")]
    [RequirePermission("platform.tenants.read")]
    [ProducesResponseType<IReadOnlyList<TenantDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TenantDto>>> ListTenants(
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
        => Ok(await queries.Query(new ListTenantsQuery(skip, take), ct));

    [HttpGet("tenants/{tenantId:guid}")]
    [RequirePermission("platform.tenants.read")]
    [ProducesResponseType<TenantDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantDto>> GetTenant(Guid tenantId, CancellationToken ct)
        => Ok(await queries.Query(new GetTenantQuery(tenantId), ct));

    // ---- Users in a tenant ---------------------------------------------------------------------

    [HttpGet("tenants/{tenantId:guid}/users")]
    [RequirePermission("tenant.users.read")]
    [ProducesResponseType<IReadOnlyList<UserListItemDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UserListItemDto>>> ListUsers(
        Guid tenantId, [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
        => Ok(await queries.Query(new ListTenantUsersQuery(tenantId, skip, take), ct));

    [HttpPost("tenants/{tenantId:guid}/users")]
    [RequirePermission("tenant.users.create")]
    [ProducesResponseType<CreateUserResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CreateUserResult>> CreateUser(
        Guid tenantId, [FromBody] CreateUserRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new CreateUserCommand(tenantId, request), ct);
        return CreatedAtAction(nameof(ListUsers), new { tenantId }, result);
    }

    // ---- Roles ---------------------------------------------------------------------------------

    [HttpGet("roles")]
    [RequirePermission("tenant.users.read")]
    [ProducesResponseType<IReadOnlyList<RoleDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RoleDto>>> ListRoles(
        [FromQuery] Guid? tenantId = null, CancellationToken ct = default)
        => Ok(await queries.Query(new ListRolesQuery(tenantId), ct));

    [HttpPost("roles")]
    [RequirePermission("platform.roles.manage")]
    [ProducesResponseType<CreateRoleResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CreateRoleResult>> CreateRole([FromBody] CreateRoleRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new CreateRoleCommand(request), ct);
        return CreatedAtAction(nameof(ListRoles), new { tenantId = request.TenantId }, result);
    }

    [HttpPost("role-assignments")]
    [RequirePermission("tenant.roles.assign")]
    [ProducesResponseType<AssignRoleResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AssignRoleResult>> AssignRole([FromBody] AssignRoleRequest request, CancellationToken ct)
        => Ok(await commands.Send(new AssignRoleCommand(request), ct));

    /// <summary>Revoke a role assignment (soft — sets revoked_at/by/reason; the row is never deleted).</summary>
    [HttpPost("role-assignments/revoke")]
    [RequirePermission("tenant.roles.assign")]
    [ProducesResponseType<RevokeRoleResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RevokeRoleResult>> RevokeRole([FromBody] RevokeRoleRequest request, CancellationToken ct)
        => Ok(await commands.Send(new RevokeRoleCommand(request), ct));

    // ---- Permission overrides (deny-wins, reason mandatory) ------------------------------------

    [HttpPost("permission-overrides")]
    [RequirePermission("platform.overrides.grant")]
    [ProducesResponseType<SetOverrideResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SetOverrideResult>> SetOverride([FromBody] SetOverrideRequest request, CancellationToken ct)
        => Ok(await commands.Send(new SetOverrideCommand(request), ct));
}
