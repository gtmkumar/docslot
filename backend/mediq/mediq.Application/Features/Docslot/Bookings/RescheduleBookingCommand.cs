using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Domain.Docslot;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;

namespace mediq.Application.Features.Docslot.Bookings;

/// <summary>
/// Reschedules a booking to a new slot. Honors the domain contract that <c>rescheduled</c> is terminal: the
/// OLD booking is marked <c>rescheduled</c> and its slot capacity freed, while a NEW booking is minted on the
/// new slot carrying the patient / department / behalf identity / patient-consent forward and linked via
/// <c>rescheduled_from_booking_id</c>. The new booking goes through the shared
/// <see cref="IBookingCreationService"/> (NOT a nested command dispatch — that would collide on the
/// idempotency cache slot), so it inherits the cutoff guard, slot hold, OPD token, audit, and event.
/// </summary>
public sealed record RescheduleBookingCommand(Guid TenantId, RescheduleBookingRequest Request)
    : ICommand<RescheduleBookingResult>, IRequireIdempotency;

public sealed record RescheduleBookingRequest(
    Guid BookingId,
    Guid NewSlotId,
    Guid? NewDoctorId,
    string? Reason,
    string? IdempotencyKey) : IIdempotentRequest;

public sealed record RescheduleBookingResult(
    Guid OldBookingId, Guid NewBookingId, string? NewBookingNumber, int? TokenNumber);

public sealed class RescheduleBookingValidator : AbstractValidator<RescheduleBookingCommand>
{
    public RescheduleBookingValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.BookingId).NotEmpty();
        RuleFor(x => x.Request.NewSlotId).NotEmpty();
    }
}

public sealed class RescheduleBookingCommandHandler(
    IBookingRepository bookings,
    IBookingCreationService creator,
    ISlotHoldService slotHolds,
    IBookingEventPublisher events,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx,
    IClock clock)
    : ICommandHandler<RescheduleBookingCommand, RescheduleBookingResult>
{
    public async Task<RescheduleBookingResult> Handle(RescheduleBookingCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var req = command.Request;

        var old = await bookings.GetByIdAsync(req.BookingId, command.TenantId, ct)
            ?? throw new KeyNotFoundException("Booking not found.");

        // Only an active (pending/confirmed) booking can be rescheduled; a checked-in/terminal one cannot.
        if (old.Status is not (BookingStatus.Pending or BookingStatus.Confirmed))
            throw new mediq.Utilities.Exceptions.BusinessRuleException(
                $"A {old.Status.ToToken()} booking cannot be rescheduled.");

        // Don't reschedule a behalf booking that hasn't cleared patient consent yet.
        if (old.AwaitingPatientConsent)
            throw new mediq.Utilities.Exceptions.BusinessRuleException(
                "This behalf booking is awaiting patient consent and cannot be rescheduled yet.");

        if (string.IsNullOrWhiteSpace(old.PatientPhoneAtBooking))
            throw new mediq.Utilities.Exceptions.BusinessRuleException("The booking has no patient phone to rebook with.");

        // Mint the NEW booking first (so a failure here leaves the old one untouched). Carry identity +
        // consent forward — a previously-confirmed behalf consent does NOT require a fresh OTP for a time move.
        var createReq = new CreateBookingRequest(
            SlotId: req.NewSlotId,
            DoctorId: req.NewDoctorId ?? old.DoctorId,
            DepartmentId: old.DepartmentId,
            PatientPhone: old.PatientPhoneAtBooking!,
            PatientName: old.PatientNameAtBooking,
            PatientAge: old.PatientAgeAtBooking,
            PatientGender: null,
            BookingType: old.BookingType,
            BookedVia: old.BookedVia,
            ChiefComplaint: old.ChiefComplaint,
            IssueOpdToken: true,
            IdempotencyKey: req.IdempotencyKey,
            BookedByType: old.BookedByType,
            BehalfRelation: old.BehalfRelation,
            BehalfBookerPhone: old.BehalfBookerPhone,
            PatientConsentStatus: old.PatientConsentStatus,
            RescheduledFromBookingId: old.BookingId);

        var created = await creator.CreateAsync(command.TenantId, createReq, now, ct);

        // Terminate the old booking and return its slot capacity.
        old.MarkRescheduled(ctx.UserId, now);
        await slotHolds.ReleaseSlotCapacityAsync(old.SlotId, now, ct);

        await audit.RecordAsync(new AuditEntry(
            "reschedule", "booking", old.BookingId, old.BookingNumber, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Rescheduled to booking {created.BookingId} on slot {req.NewSlotId}"
                           + (string.IsNullOrWhiteSpace(req.Reason) ? "" : $" — {req.Reason}")), ct);

        await events.PublishAsync(BookingEventTypes.Rescheduled, command.TenantId, old.BookingId, old.BookingNumber,
            new { old_booking_id = old.BookingId, new_booking_id = created.BookingId, new_slot_id = req.NewSlotId }, ct);

        return new RescheduleBookingResult(old.BookingId, created.BookingId, created.BookingNumber, created.TokenNumber);
    }
}
