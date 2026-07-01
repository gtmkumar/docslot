using mediq.Api.Authorization;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Security;
using mediq.SharedDataModel.Docslot.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// Tenant SECURITY-POLICY management surface (issue #91, Phase C of epic #80). The policy lives in
/// <c>platform.tenants.settings-&gt;'security'</c> (no new table); the IP allow-list reuses
/// <c>platform.ip_allowlist</c>. tenant_id ALWAYS comes from the signed JWT (never a header/param), so a
/// caller can only ever manage their own tenant. Every field written here is REALLY enforced at login /
/// password-set / patient-read — a stored toggle that nothing checks would be a compliance failure.
/// </summary>
[ApiController]
[Route("api/v1/security")]
[Authorize]
public sealed class SecurityPolicyController(
    ICommandDispatcher commands, IQueryDispatcher queries, ICurrentUserContext currentUser) : ControllerBase
{
    /// <summary>Read the effective policy (defaults merged) + the derived pending-2FA-enrolment staff count.</summary>
    [HttpGet("policy")]
    [RequirePermission("tenant.settings.read")]
    [ProducesResponseType<SecurityPolicyDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SecurityPolicyDto>> GetPolicy(CancellationToken ct)
        => Ok(await queries.Query(new GetSecurityPolicyQuery(RequireTenant()), ct));

    /// <summary>Update the policy (ranges validated: min length 8..128, idle 1..1440, hours HH:mm).</summary>
    [HttpPut("policy")]
    [RequirePermission("tenant.settings.update")]
    [ProducesResponseType<SecurityPolicyDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SecurityPolicyDto>> UpdatePolicy([FromBody] UpdateSecurityPolicyRequest request, CancellationToken ct)
        => Ok(await commands.Send(new UpdateSecurityPolicyCommand(RequireTenant(), request), ct));

    /// <summary>List this tenant's IP allow-list entries (CIDRs are network metadata, not secrets).</summary>
    [HttpGet("ip-allowlist")]
    [RequirePermission("platform.ip_allowlist.manage")]
    [ProducesResponseType<IReadOnlyList<IpAllowlistEntryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<IpAllowlistEntryDto>>> ListAllowlist(CancellationToken ct)
        => Ok(await queries.Query(new ListIpAllowlistQuery(RequireTenant()), ct));

    /// <summary>Add a CIDR entry to this tenant's allow-list. Returns the new entry id.</summary>
    [HttpPost("ip-allowlist")]
    [RequirePermission("platform.ip_allowlist.manage")]
    [ProducesResponseType<Guid>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<Guid>> AddAllowlist([FromBody] AddIpAllowlistRequest request, CancellationToken ct)
        => Ok(await commands.Send(new AddIpAllowlistCommand(RequireTenant(), request), ct));

    /// <summary>Deactivate (soft-delete) an allow-list entry — 404 unless it belongs to this tenant.</summary>
    [HttpDelete("ip-allowlist/{allowlistId:guid}")]
    [RequirePermission("platform.ip_allowlist.manage")]
    [ProducesResponseType<bool>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<bool>> RemoveAllowlist(Guid allowlistId, CancellationToken ct)
        => Ok(await commands.Send(new RemoveIpAllowlistCommand(RequireTenant(), allowlistId), ct));

    private Guid RequireTenant() =>
        currentUser.TenantId ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");
}
