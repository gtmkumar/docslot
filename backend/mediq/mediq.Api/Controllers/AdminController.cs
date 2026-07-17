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

    /// <summary>Full editable detail for one tenant (superset of the list DTO): adds legal name, primary phone,
    /// state and PIN so the edit form pre-fills every field. tenant_code/tenant_type are read-only display fields.</summary>
    [HttpGet("tenants/{tenantId:guid}")]
    [RequirePermission("platform.tenants.read")]
    [ProducesResponseType<TenantDetailDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TenantDetailDto>> GetTenant(Guid tenantId, CancellationToken ct)
        => Ok(await queries.Query(new GetTenantQuery(tenantId), ct));

    /// <summary>Onboard a new tenant + mint its one-time <c>tenant_owner</c> invitation (single transaction).
    /// The response carries the plaintext invite token EXACTLY ONCE — the owner redeems it on the public
    /// accept page and sets their own password; no credential is ever created here.</summary>
    [HttpPost("tenants")]
    [RequirePermission("platform.tenants.create")]
    [ProducesResponseType<CreateTenantResult>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreateTenantResult>> CreateTenant(
        [FromBody] CreateTenantRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new CreateTenantCommand(request), ct);
        return CreatedAtAction(nameof(GetTenant), new { tenantId = result.TenantId }, result);
    }

    /// <summary>Edit a tenant's mutable attributes (display/legal name, primary contact, city/state/PIN).
    /// <c>tenant_code</c> and <c>tenant_type</c> are immutable and not accepted; <c>status</c> is NOT editable here
    /// either — suspend/reactivate is a distinct DANGEROUS action (see <see cref="SetTenantStatus"/>). Returns the
    /// updated <see cref="TenantDetailDto"/> (same detail shape as GET, so the edit form re-syncs); 404 when the
    /// tenant does not exist; 409 on conflict.</summary>
    [HttpPut("tenants/{tenantId:guid}")]
    [RequirePermission("platform.tenants.update")]
    [ProducesResponseType<TenantDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TenantDetailDto>> UpdateTenant(
        Guid tenantId, [FromBody] UpdateTenantRequest request, CancellationToken ct)
        => Ok(await commands.Send(new UpdateTenantCommand(tenantId, request), ct));

    // Suspend and reactivate are SEPARATE endpoints (the broker pattern — CommissionController
    // SuspendBroker/ActivateBroker), the transition implied by the route. BOTH are gated on the DANGEROUS
    // platform.tenants.suspend key (distinct from platform.tenants.update), per the security auditor's veto.

    /// <summary>Suspend a tenant — DANGEROUS. A reason is MANDATORY (422 without) and is persisted to
    /// <c>suspended_reason</c>. Returns the fresh <see cref="TenantDetailDto"/> (status chip + reason re-sync);
    /// 404 when the tenant does not exist.</summary>
    [HttpPut("tenants/{tenantId:guid}/suspend")]
    [RequirePermission("platform.tenants.suspend")]
    [ProducesResponseType<TenantDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TenantDetailDto>> SuspendTenant(
        Guid tenantId, [FromBody] SetTenantStatusReasonRequest request, CancellationToken ct)
        => Ok(await commands.Send(new SetTenantStatusCommand(tenantId, IsActive: false, request.Reason), ct));

    /// <summary>Reactivate a suspended tenant — DANGEROUS (same permission as suspend). Clears
    /// <c>suspended_reason</c>; no reason required. Returns the fresh <see cref="TenantDetailDto"/>;
    /// 404 when the tenant does not exist.</summary>
    [HttpPut("tenants/{tenantId:guid}/reactivate")]
    [RequirePermission("platform.tenants.suspend")]
    [ProducesResponseType<TenantDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantDetailDto>> ReactivateTenant(
        Guid tenantId, [FromBody] SetTenantStatusReasonRequest request, CancellationToken ct)
        => Ok(await commands.Send(new SetTenantStatusCommand(tenantId, IsActive: true, request.Reason), ct));

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

    /// <summary>Deactivate (revoke the user's memberships in this tenant) or reactivate them. Tenant-scoped —
    /// never flips the global users.is_active. Self-guard + last-admin guard enforced at the DB.</summary>
    [HttpPut("tenants/{tenantId:guid}/users/{userId:guid}/status")]
    [RequirePermission("tenant.users.update")]
    [ProducesResponseType<SetUserStatusResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SetUserStatusResult>> SetUserStatus(
        Guid tenantId, Guid userId, [FromBody] SetUserStatusRequest request, CancellationToken ct)
        => Ok(await commands.Send(new SetUserStatusCommand(tenantId, userId, request), ct));

    /// <summary>Edit a user's profile (full name / phone / preferred language only). Email/auth/status are
    /// never mutable here.</summary>
    [HttpPut("tenants/{tenantId:guid}/users/{userId:guid}")]
    [RequirePermission("tenant.users.update")]
    [ProducesResponseType<UpdateUserProfileResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UpdateUserProfileResult>> UpdateUserProfile(
        Guid tenantId, Guid userId, [FromBody] UpdateUserProfileRequest request, CancellationToken ct)
        => Ok(await commands.Send(new UpdateUserProfileCommand(tenantId, userId, request), ct));

    /// <summary>Reset access — force a password change + clear the lockout (flags only; no plaintext). The
    /// actor cannot reset their own access (self-guard at the DB).</summary>
    [HttpPost("tenants/{tenantId:guid}/users/{userId:guid}/reset-access")]
    [RequirePermission("tenant.users.update")]
    [ProducesResponseType<ResetAccessResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ResetAccessResult>> ResetUserAccess(
        Guid tenantId, Guid userId, [FromBody] ResetAccessRequest request, CancellationToken ct)
        => Ok(await commands.Send(new ResetAccessCommand(tenantId, userId, request), ct));

    // ---- People export + bulk import (issue #95, Phase D of epic #80) --------------------------

    /// <summary>Export the tenant's members as CSV (full name, email, roles, branch, department, status, 2FA,
    /// last active). Staff PII (not PHI) — fine for <c>tenant.users.read</c> holders. Tenant-scoped from the
    /// signed context; the file is CSV-injection-safe (RFC-4180 quoting + leading =,+,-,@ neutralisation).</summary>
    [HttpGet("tenants/{tenantId:guid}/users/export")]
    [RequirePermission("tenant.users.read")]
    [Produces("text/csv")]
    public async Task<IActionResult> ExportUsers(Guid tenantId, CancellationToken ct)
    {
        var csv = await queries.Query(new ExportTenantUsersQuery(tenantId), ct);
        return File(System.Text.Encoding.UTF8.GetBytes(csv.Content), "text/csv", csv.FileName);
    }

    /// <summary>Bulk-import members from a parsed row list (the SPA parses the CSV client-side and posts JSON).
    /// Each row is provisioned through the same escalation-safe single-user path and is individually atomic;
    /// a role is conferred subject to the R3 no-escalation guard. Returns a per-row result — one bad row never
    /// aborts the batch. Batch size is capped (oversize → 422).</summary>
    [HttpPost("tenants/{tenantId:guid}/users/bulk-import")]
    [RequirePermission("tenant.users.create")]
    [ProducesResponseType<BulkImportResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BulkImportResult>> BulkImportUsers(
        Guid tenantId, [FromBody] BulkImportUsersRequest request, CancellationToken ct)
        => Ok(await commands.Send(new BulkImportUsersCommand(tenantId, request), ct));

    // ---- Branches + membership scope (org display attribute — issue #90) -----------------------

    /// <summary>Lists a tenant's active branches — powers the People "All branches" filter + the "N branches"
    /// stat. Branches are an organizational display attribute; they never confer permissions.</summary>
    [HttpGet("tenants/{tenantId:guid}/branches")]
    [RequirePermission("tenant.users.read")]
    [ProducesResponseType<IReadOnlyList<BranchDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BranchDto>>> ListBranches(Guid tenantId, CancellationToken ct)
        => Ok(await queries.Query(new ListTenantBranchesQuery(tenantId), ct));

    /// <summary>Create a branch under the tenant. A tenant-configuration act, gated on
    /// <c>tenant.settings.update</c>. Direct own-tenant write under RLS (no permission surface).</summary>
    [HttpPost("tenants/{tenantId:guid}/branches")]
    [RequirePermission("tenant.settings.update")]
    [ProducesResponseType<CreateBranchResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CreateBranchResult>> CreateBranch(
        Guid tenantId, [FromBody] CreateBranchRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new CreateBranchCommand(tenantId, request), ct);
        return CreatedAtAction(nameof(ListBranches), new { tenantId }, result);
    }

    /// <summary>Set a member's organizational scope (branch + department) — DISPLAY ONLY. Gated on
    /// <c>tenant.users.update</c> and routed through <c>platform.set_membership_scope</c>, which writes only
    /// branch_id/department (never role_id) — so it can never change the user's effective access.</summary>
    [HttpPut("tenants/{tenantId:guid}/users/{userId:guid}/scope")]
    [RequirePermission("tenant.users.update")]
    [ProducesResponseType<SetMemberScopeResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SetMemberScopeResult>> SetMemberScope(
        Guid tenantId, Guid userId, [FromBody] SetMemberScopeRequest request, CancellationToken ct)
        => Ok(await commands.Send(new SetMemberScopeCommand(tenantId, userId, request), ct));

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
