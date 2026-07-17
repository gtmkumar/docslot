using mediq.Api.Authorization;
using mediq.Api.Caching;
using mediq.Application.Cqrs;
using mediq.Application.Features.PlatformApi.Clients;
using mediq.SharedDataModel.Docslot.PlatformApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;

namespace mediq.Api.Controllers;

/// <summary>
/// platform_api client management (developer-portal admin surface). Every route is gated by the canonical
/// platform permission <c>platform.api_clients.manage</c> (verified to exist in the seed). Secrets are
/// returned plaintext exactly once (register/rotate) and never echoed afterwards.
/// </summary>
[ApiController]
[Route("api/v1/api-clients")]
[Authorize]
public sealed class ApiClientsController(ICommandDispatcher commands, IQueryDispatcher queries) : ControllerBase
{
    [HttpGet]
    [RequirePermission("platform.api_clients.manage")]
    [ProducesResponseType<IReadOnlyList<ApiClientDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ApiClientDto>>> List(
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
        => Ok(await queries.Query(new ListApiClientsQuery(skip, take), ct));

    [HttpGet("/api/v1/api-scopes")]
    [RequirePermission("platform.api_clients.manage")]
    [OutputCache(PolicyName = OutputCachePolicies.ApiCatalog)]   // seeded registry, identical for every caller; the permission gate above still runs on cache hits (UseOutputCache is after UseAuthorization)
    [ProducesResponseType<IReadOnlyList<ScopeDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ScopeDto>>> ListScopes(CancellationToken ct)
        => Ok(await queries.Query(new ListScopesQuery(), ct));

    /// <summary>
    /// Paginated API request log (developers → Logs tab). Filterable by client and date window. Returns
    /// request metadata ONLY — method/path/status/latency/scope/client/time, never bodies, IP, or PHI.
    /// </summary>
    [HttpGet("/api/v1/api-requests")]
    [RequirePermission("platform.api_clients.manage")]
    [ProducesResponseType<ApiRequestLogPageDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiRequestLogPageDto>> ListRequests(
        [FromQuery] Guid? clientId = null, [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
        => Ok(await queries.Query(new ListApiRequestsQuery(clientId, from, to, page, pageSize), ct));

    /// <summary>Register a client (manual approval — created inactive/unverified). Returns the secret ONCE.</summary>
    [HttpPost]
    [RequirePermission("platform.api_clients.manage")]
    [ProducesResponseType<ApiClientSecretResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ApiClientSecretResult>> Register([FromBody] RegisterApiClientRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new RegisterApiClientCommand(request), ct);
        return CreatedAtAction(nameof(List), null, result);
    }

    /// <summary>Rotate the client secret. Returns the new secret ONCE.</summary>
    [HttpPost("{clientId:guid}/rotate-secret")]
    [RequirePermission("platform.api_clients.manage")]
    [ProducesResponseType<ApiClientSecretResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiClientSecretResult>> RotateSecret(Guid clientId, CancellationToken ct)
        => Ok(await commands.Send(new RotateClientSecretCommand(clientId), ct));

    /// <summary>Approve (verify+activate) or suspend a client.</summary>
    [HttpPost("{clientId:guid}/status")]
    [RequirePermission("platform.api_clients.manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetStatus(Guid clientId, [FromBody] SetClientStatusRequest request, CancellationToken ct)
    {
        await commands.Send(new SetClientStatusCommand(clientId, request), ct);
        return NoContent();
    }

    /// <summary>Grant/revoke the client's requestable scope set.</summary>
    [HttpPut("{clientId:guid}/scopes")]
    [RequirePermission("platform.api_clients.manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetScopes(Guid clientId, [FromBody] SetClientScopesRequest request, CancellationToken ct)
    {
        await commands.Send(new SetClientScopesCommand(clientId, request), ct);
        return NoContent();
    }

    /// <summary>Set per-client rate limits.</summary>
    [HttpPut("{clientId:guid}/rate-limits")]
    [RequirePermission("platform.api_clients.manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetRateLimits(Guid clientId, [FromBody] SetClientRateLimitsRequest request, CancellationToken ct)
    {
        await commands.Send(new SetClientRateLimitsCommand(clientId, request), ct);
        return NoContent();
    }
}
