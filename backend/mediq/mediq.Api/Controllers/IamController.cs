using mediq.Api.Authorization;
using mediq.Application.Cqrs;
using mediq.Application.Features.Iam;
using mediq.SharedDataModel.Docslot.Iam;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// IAM — the "Team &amp; roles" admin surface (Roles &amp; permissions). Reads project the canonical RBAC
/// catalog; writes route through the SECURITY DEFINER functions, which enforce the real authority
/// (escalation guard, system-role lock, no-escalation on duplicate) at the database. The coarse
/// <see cref="RequirePermissionAttribute"/> here is the first plane gate; the DB is the security boundary.
/// <para>
/// Plane split: read = <c>tenant.users.read</c>; assignment writes (grant/revoke/duplicate, which the DB
/// restricts to non-system roles for non-super actors) = <c>tenant.roles.assign</c>. Editing a built-in
/// role or platform-scoped permission additionally requires super_admin, enforced in the functions.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/iam")]
[Authorize]
public sealed class IamController(ICommandDispatcher commands, IQueryDispatcher queries) : ControllerBase
{
    // ---- Catalog reads -------------------------------------------------------------------------

    /// <summary>Lists the modules (privilege groups) that head the matrix.</summary>
    [HttpGet("modules")]
    [RequirePermission("tenant.users.read")]
    [ProducesResponseType<IReadOnlyList<ModuleDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ModuleDto>>> ListModules(CancellationToken ct)
        => Ok(await queries.Query(new ListModulesQuery(), ct));

    /// <summary>Lists catalog permissions, optionally narrowed to one module via <c>?module=</c>.</summary>
    [HttpGet("permissions")]
    [RequirePermission("tenant.users.read")]
    [ProducesResponseType<IReadOnlyList<PermissionDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PermissionDto>>> ListPermissions(
        [FromQuery(Name = "module")] string? module = null, CancellationToken ct = default)
        => Ok(await queries.Query(new ListPermissionsQuery(module), ct));

    // ---- Role matrix ---------------------------------------------------------------------------

    /// <summary>The full grant matrix for a role (modules → action cells). 404 if the role is unknown.</summary>
    [HttpGet("roles/{roleId:guid}/matrix")]
    [RequirePermission("tenant.users.read")]
    [ProducesResponseType<RoleMatrixDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoleMatrixDto>> GetRoleMatrix(Guid roleId, CancellationToken ct)
        => Ok(await queries.Query(new GetRoleMatrixQuery(roleId), ct));

    /// <summary>Grant a single permission to a role (matrix checkbox ON). Idempotent.</summary>
    [HttpPost("roles/{roleId:guid}/permissions/{permissionId:guid}")]
    [RequirePermission("tenant.roles.assign")]
    [ProducesResponseType<SetRolePermissionResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SetRolePermissionResult>> GrantRolePermission(
        Guid roleId, Guid permissionId, [FromBody] SetRolePermissionRequest? request, CancellationToken ct)
        => Ok(await commands.Send(
            new GrantRolePermissionCommand(roleId, permissionId, request?.TenantId, request?.Grantable ?? false), ct));

    /// <summary>Revoke a single permission from a role (matrix checkbox OFF). Idempotent.</summary>
    [HttpDelete("roles/{roleId:guid}/permissions/{permissionId:guid}")]
    [RequirePermission("tenant.roles.assign")]
    [ProducesResponseType<SetRolePermissionResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SetRolePermissionResult>> RevokeRolePermission(
        Guid roleId, Guid permissionId, [FromQuery] Guid? tenantId = null, CancellationToken ct = default)
        => Ok(await commands.Send(new RevokeRolePermissionCommand(roleId, permissionId, tenantId), ct));

    /// <summary>Duplicate a role into a new custom role, copying its grants (the "Duplicate built-in" gesture).</summary>
    [HttpPost("roles/duplicate")]
    [RequirePermission("tenant.roles.assign")]
    [ProducesResponseType<DuplicateRoleResult>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DuplicateRoleResult>> DuplicateRole(
        [FromBody] DuplicateRoleRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new DuplicateRoleCommand(request), ct);
        return CreatedAtAction(nameof(GetRoleMatrix), new { roleId = result.RoleId }, result);
    }

    // ---- Catalog plane (platform-governed): create modules + permissions -----------------------

    /// <summary>Create a module (resource_type). Platform-plane: gated on <c>platform.permissions.manage</c>.</summary>
    [HttpPost("modules")]
    [RequirePermission("platform.permissions.manage")]
    [ProducesResponseType<CreateModuleResult>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreateModuleResult>> CreateModule(
        [FromBody] CreateModuleRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new CreateModuleCommand(request), ct);
        return CreatedAtAction(nameof(ListModules), null, result);
    }

    /// <summary>Create a permission (<c>resource.action</c>). Platform-plane: gated on <c>platform.permissions.manage</c>.
    /// It becomes grantable + visible in the matrix immediately; enforcement ships with the feature that checks it.</summary>
    [HttpPost("permissions")]
    [RequirePermission("platform.permissions.manage")]
    [ProducesResponseType<CreatePermissionResult>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreatePermissionResult>> CreatePermission(
        [FromBody] CreatePermissionRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new CreatePermissionCommand(request), ct);
        return CreatedAtAction(nameof(ListPermissions), null, result);
    }

    /// <summary>Set a tenant's per-module license (the matrix "Module not licensed" gate). Commercial/platform
    /// act, gated on <c>platform.settings.update</c>. Display-only — it greys cells, never changes access.</summary>
    [HttpPut("modules/{resourceTypeId:guid}/license")]
    [RequirePermission("platform.settings.update")]
    [ProducesResponseType<SetModuleLicenseResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SetModuleLicenseResult>> SetModuleLicense(
        Guid resourceTypeId, [FromBody] SetModuleLicenseRequest request, CancellationToken ct)
        => Ok(await commands.Send(new SetModuleLicenseCommand(resourceTypeId, request), ct));

    // ---- Effective access ----------------------------------------------------------------------

    /// <summary>The effective (resolved) permission set for a user in a tenant — role grants − denies + grants.</summary>
    [HttpGet("users/{userId:guid}/effective-access")]
    [RequirePermission("tenant.users.read")]
    [ProducesResponseType<EffectiveAccessDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<EffectiveAccessDto>> GetEffectiveAccess(
        Guid userId, [FromQuery] Guid? tenantId = null, CancellationToken ct = default)
        => Ok(await queries.Query(new GetEffectiveAccessQuery(userId, tenantId), ct));
}
