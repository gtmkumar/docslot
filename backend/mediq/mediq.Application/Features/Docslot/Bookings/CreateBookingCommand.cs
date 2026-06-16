using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Domain.Docslot;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;

namespace mediq.Application.Features.Docslot.Bookings;

/// <summary>
/// Staff/walk-in booking creation. Resolves (or registers) the patient by phone (cross-tenant identity),
/// ensures the patient is linked to the tenant, places a 5-minute TTL hold on the slot, inserts the
/// booking (status 'pending'; booking_number assigned by trigger), converts the hold, optionally issues an
/// OPD token, and emits <c>docslot.booking.created</c>. Idempotency-Key required (booking is a mutation).
/// </summary>
public sealed record CreateBookingCommand(Guid TenantId, CreateBookingRequest Request)
    : ICommand<CreateBookingResult>, IRequireIdempotency;

public sealed record CreateBookingRequest(
    Guid SlotId,
    Guid DoctorId,
    Guid? DepartmentId,
    string PatientPhone,
    string? PatientName,
    short? PatientAge,
    string? PatientGender,
    string BookingType,
    string BookedVia,
    string? ChiefComplaint,
    bool IssueOpdToken,
    string? IdempotencyKey) : IIdempotentRequest;

public sealed record CreateBookingResult(Guid BookingId, string? BookingNumber, int? TokenNumber);

public sealed class CreateBookingValidator : AbstractValidator<CreateBookingCommand>
{
    private static readonly string[] ValidVia = ["whatsapp", "dashboard", "api", "walk_in", "phone_call"];
    private static readonly string[] ValidType = ["consultation", "follow_up", "test", "home_collection", "procedure", "tele_consultation"];

    public CreateBookingValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.SlotId).NotEmpty();
        RuleFor(x => x.Request.DoctorId).NotEmpty();
        RuleFor(x => x.Request.PatientPhone).NotEmpty().MaximumLength(15);
        RuleFor(x => x.Request.BookedVia).Must(v => ValidVia.Contains(v)).WithMessage("Invalid booked_via.");
        RuleFor(x => x.Request.BookingType).Must(t => ValidType.Contains(t)).WithMessage("Invalid booking_type.");
    }
}

public sealed class CreateBookingCommandHandler(
    IBookingRepository bookings,
    IPatientRepository patients,
    ISlotHoldService slotHolds,
    IOpdTokenService opdTokens,
    IBookingEventPublisher events,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx,
    IClock clock)
    : ICommandHandler<CreateBookingCommand, CreateBookingResult>
{
    private static readonly TimeSpan HoldTtl = TimeSpan.FromMinutes(5);   // FR-BOOK-02

    public async Task<CreateBookingResult> Handle(CreateBookingCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var req = command.Request;

        // Patient: cross-tenant identity by phone. Resolve or register, then ensure tenant link.
        var patient = await patients.GetByPhoneAsync(req.PatientPhone, ct);
        var patientId = patient?.PatientId
            ?? await patients.CreateAsync(req.PatientPhone, req.PatientName, req.PatientAge, req.PatientGender, "en", now, ct);
        if (!await patients.IsLinkedToTenantAsync(patientId, command.TenantId, ct))
            await patients.LinkToTenantAsync(patientId, command.TenantId, now, ct);

        // Hold the slot for 5 minutes (atomic capacity reservation; throws if unavailable).
        var hold = await slotHolds.HoldAsync(command.TenantId, req.SlotId, HoldTtl, now, ct);

        var booking = Booking.Create(
            command.TenantId, req.SlotId, patientId, req.DoctorId, req.DepartmentId,
            req.BookingType, req.BookedVia, req.PatientName, req.PatientPhone, req.PatientAge,
            req.ChiefComplaint, notes: null, ctx.UserId, now);

        // Insert + flush immediately: the DB trigger assigns booking_number, and the hold-conversion /
        // OPD token below reference the booking row by FK.
        var bookingNumber = await bookings.AddAndSaveAsync(booking, ct);

        // Convert the hold (consume slot capacity) now that the booking row exists.
        await slotHolds.ConvertAsync(hold.HoldId, booking.BookingId, now, ct);

        int? tokenNumber = null;
        if (req.IssueOpdToken)
            tokenNumber = await opdTokens.IssueAsync(
                command.TenantId, booking.BookingId, req.DoctorId,
                DateOnly.FromDateTime(now), now, ct);

        await audit.RecordAsync(new AuditEntry(
            "create", "booking", booking.BookingId, bookingNumber, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Created booking for patient {patientId} on slot {req.SlotId}"), ct);

        await events.PublishAsync(BookingEventTypes.Created, command.TenantId, booking.BookingId, bookingNumber,
            new { booking_id = booking.BookingId, patient_id = patientId, slot_id = req.SlotId }, ct);

        return new CreateBookingResult(booking.BookingId, bookingNumber, tokenNumber);
    }
}
