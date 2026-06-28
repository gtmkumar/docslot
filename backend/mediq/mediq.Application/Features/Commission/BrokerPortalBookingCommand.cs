using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Docslot.Bookings;
using mediq.Application.Features.Docslot.WhatsApp;
using mediq.SharedDataModel.Docslot.Commission;
using mediq.Utilities.Exceptions;
using DDocslot = mediq.Domain.Docslot;

namespace mediq.Application.Features.Commission;

/// <summary>
/// Broker self-service booking (Phase-2 attribution path #2). A logged-in broker books on behalf of a referred
/// patient: we create a BEHALF booking (the broker is the Care-Partner booker; patient consent is pending —
/// DPDP), record an AUTO-VERIFIED <c>broker_portal_booking</c> attribution (the broker demonstrably made the
/// booking — no patient confirmation needed for the credit), and dispatch the patient a consent OTP. Consent
/// confirm → the booking proceeds and the attribution earns on completion; consent deny → the booking is
/// cancelled and the existing reversal path claws the attribution back. All in ONE tenant-scoped UoW.
/// <para>
/// IDOR-safe: <c>BrokerId</c> comes from the server-signed broker_id claim (RequireOwnBroker) and <c>TenantId</c>
/// from the JWT tenant — never client-supplied. The broker must be active + linked to the tenant.
/// </para>
/// </summary>
public sealed record BrokerPortalBookingCommand(Guid TenantId, Guid BrokerId, CreateBrokerBookingRequest Request)
    : ICommand<BrokerBookingResult>;

public sealed class BrokerPortalBookingValidator : AbstractValidator<BrokerPortalBookingCommand>
{
    public BrokerPortalBookingValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.BrokerId).NotEmpty();
        RuleFor(x => x.Request.PatientPhone).NotEmpty();
        RuleFor(x => x.Request.SlotId).NotEmpty();
        RuleFor(x => x.Request.DoctorId).NotEmpty();
    }
}

public sealed class BrokerPortalBookingCommandHandler(
    IBrokerRepository brokers,
    IBookingCreationService bookingCreation,
    IAttributionRepository attributions,
    IWhatsAppCatalogReadService catalog,
    IPatientConsentService consent,
    ICommandDispatcher commands,
    IClock clock)
    : ICommandHandler<BrokerPortalBookingCommand, BrokerBookingResult>
{
    public async Task<BrokerBookingResult> Handle(BrokerPortalBookingCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var req = command.Request;

        // Broker must be a real, earning broker linked to THIS tenant (fail fast before any write).
        var broker = await brokers.GetByIdAsync(command.BrokerId, ct)
            ?? throw new ForbiddenException("No broker identity for this session.");
        if (!broker.CanEarn)
            throw new ForbiddenException("Broker is inactive or blacklisted; cannot book.");
        if (!await brokers.IsLinkedToTenantAsync(command.BrokerId, command.TenantId, ct))
            throw new ForbiddenException("Broker is not linked to this tenant.");

        // Create the BEHALF booking — the broker is the Care-Partner booker; consent is pending; the OPD token
        // is deferred until the patient approves (the booking is not yet confirmed). Direct discount never
        // applies (this is an attributed booking).
        var bookingReq = new CreateBookingRequest(
            SlotId: req.SlotId,
            DoctorId: req.DoctorId,
            DepartmentId: req.DepartmentId,
            PatientPhone: req.PatientPhone,
            PatientName: req.PatientName,
            PatientAge: req.PatientAge,
            PatientGender: req.PatientGender,
            BookingType: "consultation",
            BookedVia: "broker_portal",
            ChiefComplaint: req.ChiefComplaint,
            IssueOpdToken: false,
            IdempotencyKey: null,
            BookedByType: DDocslot.BookedByType.Behalf,
            BehalfRelation: DDocslot.BehalfRelation.CarePartner,
            BehalfBookerPhone: broker.Phone,
            PatientConsentStatus: DDocslot.PatientConsentStatus.Pending);

        var booking = await bookingCreation.CreateAsync(command.TenantId, bookingReq, now, ct);

        // Auto-verified attribution (broker_portal_booking ⇒ auto_verified). Nested dispatch reuses the ambient
        // tx (BeginTenantScopeAsync reuses it; non-idempotent command). Discount-exclusivity trigger is moot here.
        var attribution = await commands.Send(new CreateAttributionCommand(
            command.TenantId, new CreateAttributionRequest(booking.BookingId, command.BrokerId, "broker_portal_booking", null, null)), ct);

        // DPDP: a third party (the broker) booked for the patient → send the patient a consent OTP. The
        // single-live-OTP guard supersedes any pending claim for this patient so their reply is unambiguous.
        var contact = await attributions.GetBookingPatientContactAsync(booking.BookingId, command.TenantId, ct);
        var lang = contact?.PreferredLanguage ?? "en";
        var tenantName = await catalog.GetTenantDisplayNameAsync(command.TenantId, ct)
            ?? (lang == WhatsAppTemplates.Hi ? "हमारा क्लिनिक" : "our clinic");

        await consent.SendForBehalfBookingAsync(new ConsentSendRequest(
            TenantId: command.TenantId,
            BookingId: booking.BookingId,
            PatientId: null,
            PatientPhone: req.PatientPhone,
            BookerPhone: broker.Phone,
            Relation: DDocslot.BehalfRelation.CarePartner,
            TenantName: tenantName,
            BookerName: broker.FullName,
            DoctorName: null,
            SlotLabel: null,
            Lang: lang,
            NowUtc: now), ct);

        return new BrokerBookingResult(booking.BookingId, booking.BookingNumber, attribution.AttributionId, "awaiting_patient_consent");
    }
}
