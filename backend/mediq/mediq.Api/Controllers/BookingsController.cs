using mediq.Api.Authorization;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Docslot.Bookings;
using mediq.Application.Features.Docslot.Queries;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// Reception-desk booking surface (the Dashboard contract). Reads are gated by <c>docslot.booking.read</c>;
/// mutations by the matching <c>docslot.booking.*</c> permission and REQUIRE an Idempotency-Key (enforced
/// by the pipeline). All reads are tenant-scoped with masked phone (PHI).
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class BookingsController(
    ICommandDispatcher commands,
    IQueryDispatcher queries,
    ICurrentUserContext currentUser,
    IIdempotencyContext idempotency) : ControllerBase
{
    [HttpGet("dashboard/summary")]
    [RequirePermission("docslot.booking.read")]
    [ProducesResponseType<DashboardSummaryDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardSummaryDto>> Summary(CancellationToken ct)
        => Ok(await queries.Query(new GetDashboardSummaryQuery(RequireTenant()), ct));

    [HttpGet("bookings")]
    [RequirePermission("docslot.booking.read")]
    [ProducesResponseType<IReadOnlyList<BookingListItemDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BookingListItemDto>>> List(
        [FromQuery] string? status, [FromQuery] DateOnly? date, [FromQuery] Guid? doctorId,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var (items, total) = await queries.Query(
            new ListBookingsQuery(new BookingListFilter(RequireTenant(), status, date, doctorId, skip, take)), ct);
        Response.Headers["X-Total-Count"] = total.ToString();
        return Ok(items);
    }

    [HttpGet("bookings/{bookingId:guid}")]
    [RequirePermission("docslot.booking.read")]
    [ProducesResponseType<BookingListItemDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BookingListItemDto>> Get(Guid bookingId, CancellationToken ct)
        => Ok(await queries.Query(new GetBookingQuery(RequireTenant(), bookingId), ct));

    [HttpGet("bookings/{bookingId:guid}/conversation")]
    [RequirePermission("docslot.booking.read")]
    [ProducesResponseType<IReadOnlyList<ConversationMessageDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ConversationMessageDto>>> Conversation(Guid bookingId, CancellationToken ct)
        => Ok(await queries.Query(new GetConversationQuery(RequireTenant(), bookingId), ct));

    // ---- Mutations (Idempotency-Key required) ------------------------------------------------

    /// <summary>Create a booking (staff/walk-in). Takes a 5-min slot hold. Gated by docslot.booking.create.</summary>
    [HttpPost("bookings")]
    [RequirePermission("docslot.booking.create")]
    [ProducesResponseType<CreateBookingResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CreateBookingResult>> Create([FromBody] CreateBookingRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new CreateBookingCommand(RequireTenant(), request), ct);
        return CreatedAtAction(nameof(Get), new { bookingId = result.BookingId }, result);
    }

    /// <summary>Approve a pending booking. Gated by docslot.booking.approve.</summary>
    [HttpPost("bookings/{bookingId:guid}/approve")]
    [RequirePermission("docslot.booking.approve")]
    public Task<ActionResult<BookingActionResultDto>> Approve(Guid bookingId, CancellationToken ct)
        => ActAsync(bookingId, BookingActionType.Approve, null, ct);

    /// <summary>Cancel a booking (reason required). Gated by docslot.booking.cancel.</summary>
    [HttpPost("bookings/{bookingId:guid}/cancel")]
    [RequirePermission("docslot.booking.cancel")]
    public Task<ActionResult<BookingActionResultDto>> Cancel(Guid bookingId, [FromBody] ReasonBody body, CancellationToken ct)
        => ActAsync(bookingId, BookingActionType.Cancel, body.Reason, ct);

    /// <summary>Mark a booking as no-show. Gated by docslot.booking.no_show (Slice 08 added this dedicated
    /// key; granted to every role that already held docslot.booking.complete, so no caller loses access).</summary>
    [HttpPost("bookings/{bookingId:guid}/no-show")]
    [RequirePermission("docslot.booking.no_show")]
    public Task<ActionResult<BookingActionResultDto>> NoShow(Guid bookingId, CancellationToken ct)
        => ActAsync(bookingId, BookingActionType.MarkNoShow, null, ct);

    /// <summary>Mark a confirmed booking complete. Gated by docslot.booking.complete.</summary>
    [HttpPost("bookings/{bookingId:guid}/complete")]
    [RequirePermission("docslot.booking.complete")]
    public Task<ActionResult<BookingActionResultDto>> Complete(Guid bookingId, CancellationToken ct)
        => ActAsync(bookingId, BookingActionType.Complete, null, ct);

    private async Task<ActionResult<BookingActionResultDto>> ActAsync(
        Guid bookingId, BookingActionType action, string? reason, CancellationToken ct)
    {
        var request = new ApprovalActionRequest(bookingId, action, reason, idempotency.Key);
        var result = await commands.Send(new BookingActionCommand(RequireTenant(), request), ct);

        // Re-stamp the replay flag from the idempotency marker (the behavior served a cached response).
        if (idempotency is IIdempotencyReplayMarker { WasReplayed: true })
            result = result with { WasReplayed = true };

        return Ok(result);
    }

    private Guid RequireTenant() =>
        currentUser.TenantId ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");

    public sealed record ReasonBody(string Reason);
}
