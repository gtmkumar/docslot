using mediq.Api.Authorization;
using mediq.Application.Cqrs;
using mediq.Application.Features.Invitations;
using mediq.SharedDataModel.Docslot.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// Invitations (issue #89, epic #80 Phase C) — token-based tenant onboarding, a NEW capability that sits
/// ALONGSIDE the direct-add invite (<see cref="AdminController.CreateUser"/>). An admin mints a single-use,
/// hashed, expiring token; the invitee redeems it UNAUTHENTICATED to self-provision (their own password +
/// display name) and receive a pre-vetted role.
/// <para>
/// The management endpoints bind the tenant from the SIGNED JWT (never the route), gate on
/// <c>tenant.users.create</c> / <c>tenant.users.read</c>, and route writes through the SECURITY DEFINER
/// functions that enforce the actor's permission + the R3 no-escalation guard at the DB. The plaintext token
/// is returned EXACTLY ONCE (create/resend) — only its hash is persisted, so it is never re-fetchable. The
/// accept endpoint is <see cref="AllowAnonymousAttribute"/>: the token IS the authorization.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class InvitationsController(ICommandDispatcher commands, IQueryDispatcher queries) : ControllerBase
{
    /// <summary>Mint an invitation. Returns the invitation + the ONE-TIME plaintext token (hand it to the send
    /// step now; it is unrecoverable afterwards). A pre-attached role must be one the actor may confer (R3).</summary>
    [HttpPost("tenants/{tenantId:guid}/invitations")]
    [RequirePermission("tenant.users.create")]
    [ProducesResponseType<InvitationTokenResult>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<InvitationTokenResult>> Create(
        Guid tenantId, [FromBody] CreateInvitationRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new CreateInvitationCommand(tenantId, request), ct);
        return CreatedAtAction(nameof(List), new { tenantId }, result);
    }

    /// <summary>List a tenant's invitations (optionally filtered by <c>?status=</c>). Never returns the token/hash.
    /// Tenant is bound from the signed context + RLS — only ever the caller's own tenant.</summary>
    [HttpGet("tenants/{tenantId:guid}/invitations")]
    [RequirePermission("tenant.users.read")]
    [ProducesResponseType<InvitationListDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<InvitationListDto>> List(
        Guid tenantId, [FromQuery] string? status = null, CancellationToken ct = default)
        => Ok(await queries.Query(new ListInvitationsQuery(status), ct));

    /// <summary>Resend — rotate the token (invalidating the prior one) + extend expiry, bumping resend_count.
    /// Returns a NEW one-time token. Pending-only.</summary>
    [HttpPost("tenants/{tenantId:guid}/invitations/{id:guid}/resend")]
    [RequirePermission("tenant.users.create")]
    [ProducesResponseType<InvitationTokenResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<InvitationTokenResult>> Resend(
        Guid tenantId, Guid id, CancellationToken ct)
        => Ok(await commands.Send(new ResendInvitationCommand(tenantId, id), ct));

    /// <summary>Revoke a pending invitation (status=revoked). Idempotent.</summary>
    [HttpPost("tenants/{tenantId:guid}/invitations/{id:guid}/revoke")]
    [RequirePermission("tenant.users.create")]
    [ProducesResponseType<RevokeInvitationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<RevokeInvitationResult>> Revoke(
        Guid tenantId, Guid id, CancellationToken ct)
        => Ok(await commands.Send(new RevokeInvitationCommand(tenantId, id), ct));

    /// <summary>Accept an invitation — UNAUTHENTICATED. The token IS the authorization: the invitee sets their
    /// own display name + password and is provisioned + granted the pre-vetted role. Single-use. Any invalid /
    /// expired / revoked / already-used token returns one generic 422 (no enumeration).</summary>
    [HttpPost("invitations/accept")]
    [AllowAnonymous]
    [ProducesResponseType<AcceptInvitationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AcceptInvitationResult>> Accept(
        [FromBody] AcceptInvitationRequest request, CancellationToken ct)
        => Ok(await commands.Send(new AcceptInvitationCommand(request), ct));
}
