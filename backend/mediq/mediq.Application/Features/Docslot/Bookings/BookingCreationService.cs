using mediq.Application.Abstractions;
using mediq.Domain.Docslot;

namespace mediq.Application.Features.Docslot.Bookings;

/// <summary>
/// The reusable booking-creation core (patient resolve/link → cutoff guard → slot hold → insert → convert →
/// OPD token → audit → event). Extracted so BOTH <see cref="CreateBookingCommandHandler"/> and the reschedule
/// handler can mint a booking WITHOUT nesting the command pipeline (a nested dispatch would collide on the
/// idempotency cache slot, which is keyed by the HTTP endpoint+key). Runs in the CALLER's UnitOfWork.
/// </summary>
public interface IBookingCreationService
{
    Task<CreateBookingResult> CreateAsync(Guid tenantId, CreateBookingRequest req, DateTime nowUtc, CancellationToken ct);
}

public sealed class BookingCreationService(
    IBookingRepository bookings,
    IPatientRepository patients,
    ISlotHoldService slotHolds,
    IOpdTokenService opdTokens,
    IBookingEventPublisher events,
    ISettingsReadService settings,
    IDirectDiscountService directDiscount,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx)
    : IBookingCreationService
{
    private static readonly TimeSpan HoldTtl = TimeSpan.FromMinutes(5);   // FR-BOOK-02

    public async Task<CreateBookingResult> CreateAsync(Guid tenantId, CreateBookingRequest req, DateTime now, CancellationToken ct)
    {
        // Enforce the tenant's configurable booking cutoff (FR-BOOK): the slot must be at least
        // BookingCutoffHours in the future. Authoritative guard for every channel (staff + WhatsApp + reschedule).
        await BookingCutoff.EnsureSlotBeyondCutoffAsync(settings, slotHolds, tenantId, req.SlotId, now, ct);

        // Patient: cross-tenant identity by phone. Resolve or register, then ensure tenant link.
        var patient = await patients.GetByPhoneAsync(req.PatientPhone, ct);
        var patientId = patient?.PatientId
            ?? await patients.CreateAsync(req.PatientPhone, req.PatientName, req.PatientAge, req.PatientGender, "en", now, ct);
        if (!await patients.IsLinkedToTenantAsync(patientId, tenantId, ct))
            await patients.LinkToTenantAsync(patientId, tenantId, now, ct);

        // Hold the slot for 5 minutes (atomic capacity reservation; throws if unavailable or if the slot
        // doesn't belong to req.DoctorId — the (slot,doctor) consistency guard).
        var hold = await slotHolds.HoldAsync(tenantId, req.SlotId, req.DoctorId, HoldTtl, now, ct);

        var booking = Booking.Create(
            tenantId, req.SlotId, patientId, req.DoctorId, req.DepartmentId,
            req.BookingType, req.BookedVia, req.PatientName, req.PatientPhone, req.PatientAge,
            req.ChiefComplaint, notes: null, ctx.UserId, now,
            bookedByType: req.BookedByType, behalfRelation: req.BehalfRelation,
            behalfBookerPhone: req.BehalfBookerPhone, patientConsentStatus: req.PatientConsentStatus,
            rescheduledFromBookingId: req.RescheduledFromBookingId);

        // Insert + flush immediately: the DB trigger assigns booking_number, and the hold-conversion /
        // OPD token below reference the booking row by FK.
        var bookingNumber = await bookings.AddAndSaveAsync(booking, ct);

        // Convert the hold (consume slot capacity) now that the booking row exists.
        await slotHolds.ConvertAsync(hold.HoldId, booking.BookingId, now, ct);

        int? tokenNumber = null;
        if (req.IssueOpdToken)
            tokenNumber = await opdTokens.IssueAsync(
                tenantId, booking.BookingId, req.DoctorId, DateOnly.FromDateTime(now), now, ct);

        // Direct-patient flywheel: a broker-less booking can take a discount funded from the commission pool.
        // Writing it makes the booking ineligible for any broker attribution (DB trigger) — no double-dip.
        if (req.ApplyDirectDiscount)
            await directDiscount.ApplyAsync(tenantId, booking.BookingId, now, ct);

        await audit.RecordAsync(new AuditEntry(
            "create", "booking", booking.BookingId, bookingNumber, ctx.UserId, tenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Created booking for patient {patientId} on slot {req.SlotId}"), ct);

        await events.PublishAsync(BookingEventTypes.Created, tenantId, booking.BookingId, bookingNumber,
            new { booking_id = booking.BookingId, patient_id = patientId, slot_id = req.SlotId }, ct);

        return new CreateBookingResult(booking.BookingId, bookingNumber, tokenNumber);
    }
}
