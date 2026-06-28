using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Domain.Docslot;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;
using mediq.SharedDataModel.Docslot.Dashboard.Enums;

namespace mediq.Application.Features.Docslot.Bookings;

/// <summary>
/// Applies an approval-queue action (approve / cancel / no-show / complete) to a booking. Idempotency is
/// enforced by the pipeline behavior (Idempotency-Key required at the API). The booking aggregate's state
/// machine rejects illegal transitions; the DB trigger logs the status change to booking_status_history
/// (we never insert history manually). On success, emits the matching integration event for webhooks.
/// </summary>
public sealed record BookingActionCommand(Guid TenantId, ApprovalActionRequest Request)
    : ICommand<BookingActionResultDto>, IRequireIdempotency;

public sealed class BookingActionValidator : AbstractValidator<BookingActionCommand>
{
    public BookingActionValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.BookingId).NotEmpty();
        RuleFor(x => x.Request.Reason)
            .NotEmpty().When(x => x.Request.Action == BookingActionType.Cancel)
            .WithMessage("A reason is required to cancel a booking.");
    }
}

public sealed class BookingActionCommandHandler(
    IBookingRepository bookings,
    ISlotHoldService slotHolds,
    IBookingEventPublisher events,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx,
    IClock clock)
    : ICommandHandler<BookingActionCommand, BookingActionResultDto>
{
    public async Task<BookingActionResultDto> Handle(BookingActionCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var req = command.Request;

        var booking = await bookings.GetByIdAsync(req.BookingId, command.TenantId, ct)
            ?? throw new KeyNotFoundException("Booking not found.");

        // DPDP fake-patient guard: a behalf booking cannot be approved until the patient has confirmed consent
        // via their WhatsApp OTP. The schema permits 'pending'/'denied'/'expired'; only 'confirmed' clears this.
        if (req.Action == BookingActionType.Approve && booking.AwaitingPatientConsent)
            throw new mediq.Utilities.Exceptions.BusinessRuleException(
                "This booking was made on someone's behalf and cannot be approved until the patient confirms consent.");

        (string EventType, DateTime At) result = req.Action switch
        {
            BookingActionType.Approve   => Apply(() => booking.Approve(now), BookingEventTypes.Confirmed, now),
            BookingActionType.CheckIn   => Apply(() => booking.CheckIn(now), BookingEventTypes.CheckedIn, now),
            BookingActionType.Cancel    => Apply(() => booking.Cancel(req.Reason!, ctx.UserId, now), BookingEventTypes.Cancelled, now),
            BookingActionType.MarkNoShow => Apply(() => booking.MarkNoShow(now), BookingEventTypes.NoShow, now),
            BookingActionType.Complete  => Apply(() => booking.Complete(now), BookingEventTypes.Completed, now),
            _ => throw new ArgumentOutOfRangeException(nameof(req.Action)),
        };

        // Cancel/no-show free the slot capacity the booking consumed at creation (ConvertAsync incremented
        // it) so the slot can be re-booked; complete/approve keep it consumed. Fixes the capacity leak.
        if (req.Action is BookingActionType.Cancel or BookingActionType.MarkNoShow)
            await slotHolds.ReleaseSlotCapacityAsync(booking.SlotId, now, ct);

        // The UnitOfWork behavior commits the tracked mutation (and the DB trigger logs history).
        await audit.RecordAsync(new AuditEntry(
            req.Action.ToString().ToLowerInvariant(), "booking", booking.BookingId, booking.BookingNumber,
            ctx.UserId, command.TenantId, ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Booking → {booking.Status.ToToken()}"), ct);

        await events.PublishAsync(result.EventType, command.TenantId, booking.BookingId, booking.BookingNumber,
            new { booking_id = booking.BookingId, status = booking.Status.ToToken() }, ct);

        return new BookingActionResultDto(
            booking.BookingId,
            BookingStatusEnumMap.ToDto(booking.Status),
            new DateTimeOffset(result.At, TimeSpan.Zero),
            WasReplayed: false);   // replay flag re-stamped by the API from the idempotency marker
    }

    private static (string, DateTime) Apply(Action mutate, string eventType, DateTime now)
    {
        mutate();
        return (eventType, now);
    }
}

/// <summary>Maps the domain booking status to the wire enum.</summary>
internal static class BookingStatusEnumMap
{
    public static mediq.SharedDataModel.Docslot.Dashboard.Enums.BookingStatus ToDto(mediq.Domain.Docslot.BookingStatus s) => s switch
    {
        mediq.Domain.Docslot.BookingStatus.Pending => mediq.SharedDataModel.Docslot.Dashboard.Enums.BookingStatus.Pending,
        mediq.Domain.Docslot.BookingStatus.Confirmed => mediq.SharedDataModel.Docslot.Dashboard.Enums.BookingStatus.Confirmed,
        mediq.Domain.Docslot.BookingStatus.CheckedIn => mediq.SharedDataModel.Docslot.Dashboard.Enums.BookingStatus.CheckedIn,
        mediq.Domain.Docslot.BookingStatus.Cancelled => mediq.SharedDataModel.Docslot.Dashboard.Enums.BookingStatus.Cancelled,
        mediq.Domain.Docslot.BookingStatus.Completed => mediq.SharedDataModel.Docslot.Dashboard.Enums.BookingStatus.Completed,
        mediq.Domain.Docslot.BookingStatus.NoShow => mediq.SharedDataModel.Docslot.Dashboard.Enums.BookingStatus.NoShow,
        mediq.Domain.Docslot.BookingStatus.Rescheduled => mediq.SharedDataModel.Docslot.Dashboard.Enums.BookingStatus.Rescheduled,
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };
}
