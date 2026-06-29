using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Domain.Docslot;
using mediq.SharedDataModel.Docslot.Clinical;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Docslot.Clinical;

/// <summary>
/// Field references for the registered encrypted prescription columns (encrypted_fields_registry).
/// </summary>
internal static class PrescriptionFields
{
    public static readonly FieldRef Diagnosis = new("docslot", "prescriptions", "diagnosis");
    public static readonly FieldRef Medications = new("docslot", "prescriptions", "medications");
    public static readonly FieldRef Examination = new("docslot", "prescriptions", "examination");
    public static readonly FieldRef ChiefComplaints = new("docslot", "prescriptions", "chief_complaints");
}

// ---- Issue prescription (encrypts clinical fields, emits docslot.prescription.issued) -------------

public sealed record IssuePrescriptionCommand(Guid TenantId, IssuePrescriptionRequest Request) : ICommand<IssuePrescriptionResult>;

public sealed class IssuePrescriptionValidator : AbstractValidator<IssuePrescriptionCommand>
{
    public IssuePrescriptionValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.BookingId).NotEmpty();
        RuleFor(x => x.Request.PatientId).NotEmpty();
        RuleFor(x => x.Request.DoctorId).NotEmpty();
        RuleFor(x => x.Request.MedicationsJson).NotEmpty();
    }
}

public sealed class IssuePrescriptionCommandHandler(
    IClinicalRepository clinical,
    IFieldEncryptionService encryption,
    IBookingEventPublisher events,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx,
    IClock clock)
    : ICommandHandler<IssuePrescriptionCommand, IssuePrescriptionResult>
{
    public async Task<IssuePrescriptionResult> Handle(IssuePrescriptionCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var encCtx = new EncryptionContext(ctx.UserId, command.TenantId, "prescription", req.PatientId, ctx.IpAddress);

        // Encrypt every registered clinical field at rest (ciphertext envelopes persisted).
        var diagnosisEnc = await EncOrNull(req.Diagnosis, PrescriptionFields.Diagnosis, command.TenantId, encCtx, ct);
        var examinationEnc = await EncOrNull(req.Examination, PrescriptionFields.Examination, command.TenantId, encCtx, ct);
        var chiefEnc = await EncOrNull(req.ChiefComplaints, PrescriptionFields.ChiefComplaints, command.TenantId, encCtx, ct);
        var medsEnc = await encryption.EncryptAsync(PrescriptionFields.Medications, command.TenantId, req.MedicationsJson, encCtx, ct);

        var prescription = Prescription.Issue(
            req.BookingId, req.PatientId, req.DoctorId, command.TenantId,
            chiefEnc, examinationEnc, diagnosisEnc, medsEnc, req.Advice, req.FollowUpInDays, clock.UtcNow);

        var number = await clinical.AddPrescriptionAsync(prescription, ct);

        await audit.RecordAsync(new AuditEntry(
            "issue", "prescription", prescription.PrescriptionId, number, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: "Prescription issued (PHI encrypted)",
            Purpose: "treatment", LegalBasis: "consent"), ct);

        // Integration event — IDs ONLY, NO PHI.
        await events.PublishAsync("docslot.prescription.issued", command.TenantId, prescription.PrescriptionId, number,
            new { prescription_id = prescription.PrescriptionId, patient_id = req.PatientId, booking_id = req.BookingId }, ct);

        return new IssuePrescriptionResult(prescription.PrescriptionId, number);
    }

    private async Task<string?> EncOrNull(string? value, FieldRef field, Guid tenantId, EncryptionContext c, CancellationToken ct)
        => string.IsNullOrEmpty(value) ? null : await encryption.EncryptAsync(field, tenantId, value, c, ct);
}

// ---- Read prescription (decrypts; purpose-of-use + consent gated) ---------------------------------

public sealed record GetPrescriptionQuery(Guid TenantId, Guid PrescriptionId, string DeclaredPurpose) : IQuery<PrescriptionDto>;

public sealed class GetPrescriptionQueryHandler(
    IClinicalRepository clinical,
    IFieldEncryptionService encryption,
    IPatientRepository patients,
    IPurposeOfUseWriter purpose,
    IBreakGlassService breakGlass,
    ICurrentUserContext ctx)
    : IQueryHandler<GetPrescriptionQuery, PrescriptionDto>
{
    public async Task<PrescriptionDto> Handle(GetPrescriptionQuery q, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        if (string.IsNullOrWhiteSpace(q.DeclaredPurpose))
            throw new mediq.Utilities.Exceptions.ValidationException(new Dictionary<string, string[]> { ["X-Purpose-Of-Use"] = ["A purpose-of-use is required to read a prescription (DPDP)."] });

        var p = await clinical.GetPrescriptionAsync(q.PrescriptionId, q.TenantId, ct)
            ?? throw new KeyNotFoundException("Prescription not found.");   // RLS also blocks cross-tenant

        // Consent gate, with break-glass override (FR-MED-03): the patient must have active consent — OR an
        // active, scoped, non-expired break-glass grant must authorize this read; otherwise refuse (403).
        var patient = await patients.GetByIdAsync(p.PatientId, ct);
        BreakGlassGrant? grant = null;
        if (patient is null || !patient.HasActiveConsent)
        {
            grant = await breakGlass.GetActiveGrantAsync(userId, q.TenantId, p.PatientId, "prescription", p.PrescriptionId, ct);
            if (grant is null)
                throw new ForbiddenException("Patient has no active consent; clinical read refused (DPDP).");
        }

        // Purpose-of-use at READ time: under a grant, record is_break_glass=true + reason so the review queue
        // surfaces the actual emergency read (not just the grant).
        await purpose.RecordAsync(new PurposeOfUseEntry(
            userId, q.TenantId, "prescription", p.PrescriptionId, q.DeclaredPurpose, null, grant is not null, grant?.Justification), ct);

        var encCtx = new EncryptionContext(userId, q.TenantId, "prescription", p.PatientId, ctx.IpAddress);
        return new PrescriptionDto(
            p.PrescriptionId, p.PrescriptionNumber, p.PatientId, p.DoctorId,
            await DecOrNull(p.ChiefComplaintsEnc, PrescriptionFields.ChiefComplaints, encCtx, ct),
            await DecOrNull(p.ExaminationEnc, PrescriptionFields.Examination, encCtx, ct),
            await DecOrNull(p.DiagnosisEnc, PrescriptionFields.Diagnosis, encCtx, ct),
            await encryption.DecryptAsync(PrescriptionFields.Medications, p.MedicationsEnc, encCtx, ct),
            p.Advice, p.FollowUpInDays, p.Status, new DateTimeOffset(DateTime.SpecifyKind(p.CreatedAt, DateTimeKind.Utc)));
    }

    private async Task<string?> DecOrNull(string? envelope, FieldRef field, EncryptionContext c, CancellationToken ct)
        => string.IsNullOrEmpty(envelope) ? null : await encryption.DecryptAsync(field, envelope, c, ct);
}

// ---- List prescriptions for a patient (headers only; no PHI body) ---------------------------------

public sealed record ListPrescriptionsQuery(Guid TenantId, Guid PatientId) : IQuery<IReadOnlyList<PrescriptionListItemDto>>;

public sealed class ListPrescriptionsQueryHandler(IClinicalRepository clinical)
    : IQueryHandler<ListPrescriptionsQuery, IReadOnlyList<PrescriptionListItemDto>>
{
    public async Task<IReadOnlyList<PrescriptionListItemDto>> Handle(ListPrescriptionsQuery q, CancellationToken ct)
    {
        var rows = await clinical.ListPrescriptionsAsync(q.TenantId, q.PatientId, ct);
        return rows.Select(r => new PrescriptionListItemDto(
            r.PrescriptionId, r.PrescriptionNumber, r.DoctorId, r.Status,
            new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)))).ToList();
    }
}
