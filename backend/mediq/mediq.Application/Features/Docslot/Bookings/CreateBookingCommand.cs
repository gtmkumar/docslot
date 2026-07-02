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
    string? IdempotencyKey,
    // Behalf-booking identity (DPDP). For a behalf booking, PatientPhone is the PATIENT's number and
    // BehalfBookerPhone is who typed it; consent defaults to 'pending' unless carried forward (reschedule).
    string BookedByType = mediq.Domain.Docslot.BookedByType.Self,
    string? BehalfRelation = null,
    string? BehalfBookerPhone = null,
    string? PatientConsentStatus = null,
    Guid? RescheduledFromBookingId = null,
    // Direct-patient flywheel: when true and a matching commission rule exists, the booking gets
    // direct_discount_pct of the would-be commission as a discount (and becomes broker-attribution-ineligible).
    bool ApplyDirectDiscount = false) : IIdempotentRequest;

public sealed record CreateBookingResult(Guid BookingId, string? BookingNumber, int? TokenNumber);

/// <summary>
/// Shared slot-timing guard (FR-BOOK). Two rules, applied on create + reschedule alike:
/// (1) NO channel may book a slot that has already started;
/// (2) the tenant's <c>BookingCutoffHours</c> lead-time applies only to PATIENT-INITIATED channels
///     (whatsapp/api) on fresh creates — staff channels (dashboard/walk_in/phone_call) and reschedules
///     bypass it, because the cutoff exists to shield the clinic from last-minute self-service bookings,
///     not to stop the front desk registering the walk-in patient standing at it.
/// A future patient self-reschedule surface must pass its real channel so the cutoff re-engages.
/// </summary>
internal static class BookingCutoff
{
    /// <summary>Channels where the patient books for themselves (the cutoff's actual target).</summary>
    private static readonly string[] SelfServiceChannels = ["whatsapp", "api"];

    public static async Task EnsureSlotBeyondCutoffAsync(
        ISettingsReadService settings, ISlotHoldService slots, Guid tenantId, Guid slotId,
        string bookedVia, bool isReschedule, DateTime nowUtc, CancellationToken ct)
    {
        var start = await slots.GetSlotStartUtcAsync(slotId, ct);
        if (start is not { } s) return;   // unknown slot → the slot-hold below raises the authoritative error
        if (s <= nowUtc)
            throw new mediq.Utilities.Exceptions.BusinessRuleException(
                "This slot has already started — pick an upcoming slot.");

        if (isReschedule || !SelfServiceChannels.Contains(bookedVia)) return;

        var cutoffHours = (await settings.GetAsync(tenantId, ct))?.AppointmentSettings.BookingCutoffHours ?? 0;
        if (cutoffHours > 0 && s < nowUtc.AddHours(cutoffHours))
            throw new mediq.Utilities.Exceptions.BusinessRuleException(
                $"Bookings must be made at least {cutoffHours} hour(s) before the appointment time.");
    }
}

public sealed class CreateBookingValidator : AbstractValidator<CreateBookingCommand>
{
    private static readonly string[] ValidVia = ["whatsapp", "dashboard", "api", "walk_in", "phone_call"];
    private static readonly string[] ValidType = ["consultation", "follow_up", "test", "home_collection", "procedure", "tele_consultation"];

    private static readonly string[] ValidBookedByType = ["self", "behalf"];

    public CreateBookingValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.SlotId).NotEmpty();
        RuleFor(x => x.Request.DoctorId).NotEmpty();
        RuleFor(x => x.Request.PatientPhone).NotEmpty().MaximumLength(15);
        RuleFor(x => x.Request.BookedVia).Must(v => ValidVia.Contains(v)).WithMessage("Invalid booked_via.");
        RuleFor(x => x.Request.BookingType).Must(t => ValidType.Contains(t)).WithMessage("Invalid booking_type.");
        RuleFor(x => x.Request.BookedByType).Must(v => ValidBookedByType.Contains(v)).WithMessage("Invalid booked_by_type.");
        // A behalf booking must carry a valid relation; a self booking must not carry one (mirrors chk_behalf_relation).
        RuleFor(x => x.Request.BehalfRelation)
            .Must(BehalfRelation.IsValid).When(x => x.Request.BookedByType == BookedByType.Behalf)
            .WithMessage("A valid behalf_relation is required for a behalf booking.");
        RuleFor(x => x.Request.BehalfRelation)
            .Empty().When(x => x.Request.BookedByType == BookedByType.Self)
            .WithMessage("A self booking must not carry a behalf_relation.");
    }
}

public sealed class CreateBookingCommandHandler(IBookingCreationService creator, IClock clock)
    : ICommandHandler<CreateBookingCommand, CreateBookingResult>
{
    public Task<CreateBookingResult> Handle(CreateBookingCommand command, CancellationToken ct) =>
        creator.CreateAsync(command.TenantId, command.Request, clock.UtcNow, ct);
}
