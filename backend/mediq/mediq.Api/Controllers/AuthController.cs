using mediq.Api.Authorization;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Auth.Impersonation;
using mediq.Application.Features.Auth.Login;
using mediq.Application.Features.Auth.Logout;
using mediq.Application.Features.Auth.Refresh;
using mediq.Application.Features.Auth.SwitchTenant;
using mediq.SharedDataModel.Docslot.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// Authentication endpoints. Login/refresh are anonymous (the gateway lets them through); logout requires
/// a valid access token. Thin controllers — they only dispatch and map the result to HTTP.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(ICommandDispatcher commands, ITokenService tokenService) : ControllerBase
{
    /// <summary>POST /api/v1/auth/login — email + password → access JWT + refresh token.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TokenResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
        => Ok(await commands.Send(new LoginCommand(request), ct));

    /// <summary>POST /api/v1/auth/refresh — rotate the refresh token, mint a new access token.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TokenResponse>> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
        => Ok(await commands.Send(new RefreshCommand(request), ct));

    /// <summary>
    /// POST /api/v1/auth/switch-tenant — securely change the active tenant. Validates membership
    /// server-side and mints a NEW access token carrying the new tenant claim. Requires authentication;
    /// the unvalidated X-Tenant-Id header path has been removed (auditor blocker).
    /// </summary>
    [HttpPost("switch-tenant")]
    [Authorize]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TokenResponse>> SwitchTenant([FromBody] SwitchTenantRequest request, CancellationToken ct)
        => Ok(await commands.Send(new SwitchTenantCommand(request), ct));

    /// <summary>
    /// POST /api/v1/auth/impersonation/begin — open an AUDITED support impersonation session (issue #3) and
    /// mint a token carrying the server-signed <c>impersonated_tenant</c> claim. Gated by
    /// <c>platform.users.impersonate</c>; the actor is the authenticated principal (never the body). The
    /// audit row is written by <c>platform.begin_impersonation()</c> before the token is minted.
    /// </summary>
    [HttpPost("impersonation/begin")]
    [RequirePermission("platform.users.impersonate")]
    [ProducesResponseType<ImpersonationResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ImpersonationResponse>> BeginImpersonation(
        [FromBody] BeginImpersonationRequest request, CancellationToken ct)
        => Ok(await commands.Send(new BeginImpersonationCommand(request), ct));

    /// <summary>
    /// POST /api/v1/auth/impersonation/end — close an impersonation session and re-mint a CLEAN token (no
    /// <c>impersonated_tenant</c> claim). Any authenticated caller may attempt it; <c>end_impersonation()</c>
    /// enforces self-close-or-<c>platform.users.impersonate</c> at the DB and writes the close audit row.
    /// </summary>
    [HttpPost("impersonation/end")]
    [Authorize]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TokenResponse>> EndImpersonation(
        [FromBody] EndImpersonationRequest request, CancellationToken ct)
        => Ok(await commands.Send(new EndImpersonationCommand(request), ct));

    /// <summary>POST /api/v1/auth/logout — revoke the current session (and the refresh chain if supplied).</summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest? request, CancellationToken ct)
    {
        var bearer = ExtractBearer();
        var accessHash = bearer is null ? string.Empty : tokenService.HashToken(bearer);
        await commands.Send(new LogoutCommand(accessHash, request?.RefreshToken), ct);
        return NoContent();
    }

    private string? ExtractBearer()
    {
        var header = Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }
}
