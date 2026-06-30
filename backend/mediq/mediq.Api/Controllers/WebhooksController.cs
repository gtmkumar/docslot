using mediq.Api.Authorization;
using mediq.Application.Cqrs;
using mediq.Application.Features.PlatformApi.Webhooks;
using mediq.SharedDataModel.Docslot.PlatformApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// Webhook subscription management + event-type registry + a synthetic publish trigger (slice 02). Gated by
/// <c>platform.api_clients.manage</c>. Signing secrets are returned plaintext exactly once on create.
/// </summary>
[ApiController]
[Route("api/v1/webhooks")]
[Authorize]
public sealed class WebhooksController(ICommandDispatcher commands, IQueryDispatcher queries) : ControllerBase
{
    [HttpGet("event-types")]
    [RequirePermission("platform.api_clients.manage")]
    [ProducesResponseType<IReadOnlyList<EventTypeDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<EventTypeDto>>> EventTypes(CancellationToken ct)
        => Ok(await queries.Query(new ListEventTypesQuery(), ct));

    [HttpGet]
    [RequirePermission("platform.api_clients.manage")]
    [ProducesResponseType<IReadOnlyList<WebhookSubscriptionDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<WebhookSubscriptionDto>>> List([FromQuery] Guid clientId, CancellationToken ct)
        => Ok(await queries.Query(new ListWebhooksQuery(clientId), ct));

    /// <summary>Create a subscription. Returns the signing secret ONCE.</summary>
    [HttpPost]
    [RequirePermission("platform.api_clients.manage")]
    [ProducesResponseType<CreateWebhookResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CreateWebhookResult>> Create([FromBody] CreateWebhookRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new CreateWebhookCommand(request), ct);
        return CreatedAtAction(nameof(List), new { clientId = request.ClientId }, result);
    }

    [HttpPut("{webhookId:guid}")]
    [RequirePermission("platform.api_clients.manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Update(Guid webhookId, [FromBody] UpdateWebhookRequest request, CancellationToken ct)
    {
        await commands.Send(new UpdateWebhookCommand(webhookId, request), ct);
        return NoContent();
    }

    [HttpDelete("{webhookId:guid}")]
    [RequirePermission("platform.api_clients.manage")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid webhookId, CancellationToken ct)
    {
        await commands.Send(new DeleteWebhookCommand(webhookId), ct);
        return NoContent();
    }

    /// <summary>
    /// POST /api/v1/webhooks/publish — synthetic event trigger (slice 02). Fans the event out through the
    /// sign→deliver→retry pipeline. Slice 03's docslot domain events replace this trigger.
    /// </summary>
    [HttpPost("publish")]
    [RequirePermission("platform.api_clients.manage")]
    [ProducesResponseType<IReadOnlyList<Guid>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<Guid>>> Publish([FromBody] PublishEventApiRequest request, CancellationToken ct)
        => Ok(await commands.Send(new PublishEventCommand(request.EventType, request.TenantId, request.PayloadJson), ct));

    /// <summary>Delivery attempts for a webhook (newest first, metadata only) — the developer-portal forensics
    /// view. Tenant-scoped through the subscription (platform_api is non-RLS). NEVER returns the payload/body/secret.</summary>
    [HttpGet("{webhookId:guid}/deliveries")]
    [RequirePermission("platform.api_clients.manage")]
    [ProducesResponseType<IReadOnlyList<WebhookDeliveryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<WebhookDeliveryDto>>> Deliveries(
        Guid webhookId, [FromQuery] string? status, [FromQuery] int take, CancellationToken ct)
        => Ok(await queries.Query(new ListWebhookDeliveriesQuery(webhookId, status, take <= 0 ? 200 : take), ct));

    /// <summary>Manually re-enqueue a failed/dead-lettered delivery so the drain re-delivers it. 404 if it isn't
    /// in the caller's tenant scope; 409 if the status isn't retryable or the subscription is inactive/auto-disabled.</summary>
    [HttpPost("deliveries/{deliveryId:guid}/retry")]
    [RequirePermission("platform.api_clients.manage")]
    [ProducesResponseType<WebhookDeliveryDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<WebhookDeliveryDto>> RetryDelivery(Guid deliveryId, CancellationToken ct)
        => Ok(await commands.Send(new RetryWebhookDeliveryCommand(deliveryId), ct));
}

/// <summary>API shape for the synthetic publish trigger.</summary>
public sealed record PublishEventApiRequest(string EventType, Guid? TenantId, string PayloadJson);
