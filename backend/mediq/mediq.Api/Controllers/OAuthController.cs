using mediq.Application.Cqrs;
using mediq.Application.Features.PlatformApi.OAuth;
using mediq.SharedDataModel.Docslot.PlatformApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// OAuth 2.0 endpoints for third-party API clients. Token/revoke are anonymous (the client authenticates
/// with its credentials in the body). Thin controllers — dispatch + map.
/// </summary>
[ApiController]
[Route("api/v1/oauth")]
public sealed class OAuthController(ICommandDispatcher commands) : ControllerBase
{
    /// <summary>POST /api/v1/oauth/token — client-credentials grant → scoped JWT.</summary>
    [HttpPost("token")]
    [AllowAnonymous]
    [ProducesResponseType<OAuthTokenResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<OAuthTokenResponse>> Token([FromBody] OAuthTokenRequest request, CancellationToken ct)
        => Ok(await commands.Send(new IssueClientTokenCommand(request), ct));

    /// <summary>POST /api/v1/oauth/revoke — revoke an issued client token (hashed lookup, idempotent).</summary>
    [HttpPost("revoke")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Revoke([FromBody] OAuthRevokeRequest request, CancellationToken ct)
    {
        await commands.Send(new RevokeClientTokenCommand(request), ct);
        return Ok();
    }
}
