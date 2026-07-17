using mediq.Api.Authorization;
using mediq.Application.Cqrs;
using mediq.Application.Features.Auth.PasswordReset;
using mediq.SharedDataModel.Docslot.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// Admin-initiated password reset — mints a one-time reset LINK for a target user (returned in the response
/// body ONLY; never logged). The admin never handles the plaintext password: the USER completes the reset via
/// <c>POST /api/v1/auth/reset-password</c>. Mirrors how <see cref="InvitationsController.Create"/> returns the
/// one-time invite link so an admin can hand it over when email delivery is offline.
/// <para>
/// Two routes: the tenant route binds the tenant from the URL (validated against the signed JWT) and gates on
/// <c>tenant.users.update</c>; the platform route (no tenant) gates on <c>platform.users.impersonate</c> — the
/// narrowest EXISTING platform-scope permission super_admin holds — so a super_admin with no active tenant can
/// still reset any user cross-tenant. Both route writes through <c>admin_request_password_reset</c>, which
/// enforces the R3 no-escalation guard (tenant route) / super_admin (platform route) at the DB.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class UserPasswordResetController(ICommandDispatcher commands) : ControllerBase
{
    /// <summary>Mint a reset link for a user in the caller's tenant. Returns the one-time link + expiry. The R3
    /// no-escalation guard blocks resetting a platform-privileged or higher-privileged user (403).</summary>
    [HttpPost("tenants/{tenantId:guid}/users/{userId:guid}/reset-password")]
    [RequirePermission("tenant.users.update")]
    [ProducesResponseType<AdminResetPasswordResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AdminResetPasswordResult>> ResetForTenantUser(
        Guid tenantId, Guid userId, CancellationToken ct)
        => Ok(await commands.Send(new AdminResetPasswordCommand(tenantId, userId), ct));

    /// <summary>Platform-scope mint (super_admin) — reset any user cross-tenant. Gated on
    /// <c>platform.users.impersonate</c>; the SQL additionally requires the actor to be super_admin.</summary>
    [HttpPost("users/{userId:guid}/reset-password")]
    [RequirePermission("platform.users.impersonate")]
    [ProducesResponseType<AdminResetPasswordResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AdminResetPasswordResult>> ResetForPlatformUser(
        Guid userId, CancellationToken ct)
        => Ok(await commands.Send(new AdminResetPasswordCommand(null, userId), ct));
}
