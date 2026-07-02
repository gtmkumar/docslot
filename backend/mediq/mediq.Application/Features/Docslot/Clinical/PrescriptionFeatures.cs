using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Domain.Docslot;
using mediq.SharedDataModel.Docslot.Clinical;
using mediq.Utilities.Exceptions;
using Microsoft.Extensions.Logging;

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
    IDrugSafetyScreeningService screening,
    IBookingEventPublisher events,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx,
    IClock clock,
    ILogger<IssuePrescriptionCommandHandler> logger)
    : ICommandHandler<IssuePrescriptionCommand, IssuePrescriptionResult>
{
    public async Task<IssuePrescriptionResult> Handle(IssuePrescriptionCommand command, CancellationToken ct)
    {
        var req = command.Request;

        // Tenant-ownership guard: doctor_id is a tenant-blind FK and docslot.doctors has no RLS, so without this
        // a caller could persist a prescription in THIS tenant referencing ANOTHER tenant's doctor_id (#71).
        if (!await clinical.DoctorBelongsToTenantAsync(req.DoctorId, command.TenantId, ct))
        {
            // Security signal (survives the tx rollback the throw triggers — logging is non-transactional, unlike
            // the audit_log). IDs only, no PHI. A repeated hit is a cross-tenant-probing indicator for the SIEM.
            logger.LogWarning(
                "SECURITY: prescription-issue rejected — doctor {DoctorId} is not a valid active doctor in tenant {TenantId} (user {UserId})",
                req.DoctorId, command.TenantId, ctx.UserId);
            throw new mediq.Utilities.Exceptions.ValidationException(
                new Dictionary<string, string[]> { ["doctorId"] = ["The referenced doctor was not found for this tenant."] });
        }

        var encCtx = new EncryptionContext(ctx.UserId, command.TenantId, "prescription", req.PatientId, ctx.IpAddress);

        // Encrypt every registered clinical field at rest (ciphertext envelopes persisted).
        var diagnosisEnc = await EncOrNull(req.Diagnosis, PrescriptionFields.Diagnosis, command.TenantId, encCtx, ct);
        var examinationEnc = await EncOrNull(req.Examination, PrescriptionFields.Examination, command.TenantId, encCtx, ct);
        var chiefEnc = await EncOrNull(req.ChiefComplaints, PrescriptionFields.ChiefComplaints, command.TenantId, encCtx, ct);
        var medsEnc = await encryption.EncryptAsync(PrescriptionFields.Medications, command.TenantId, req.MedicationsJson, encCtx, ct);

        // The signer is the authenticated caller — the schema CHECK (chk_prescriptions_signed_rows_have_signer)
        // requires a signer on any finalized row; the legacy single-shot Issue signs as the caller.
        var signerUserId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var prescription = Prescription.Issue(
            req.BookingId, req.PatientId, req.DoctorId, command.TenantId,
            chiefEnc, examinationEnc, diagnosisEnc, medsEnc, req.Advice, req.FollowUpInDays, signerUserId, clock.UtcNow);

        var number = await clinical.AddPrescriptionAsync(prescription, ct);

        // Drug-safety screen against recorded allergies + current meds (in the same UoW tx → alerts are atomic
        // with the prescription). Uses the plaintext meds we already hold; does not re-decrypt the prescription.
        await screening.ScreenPrescriptionAsync(prescription.PrescriptionId, req.PatientId, command.TenantId, req.MedicationsJson, ct);

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

// ---- Amend prescription (mint-new-supersedes-original; emits docslot.prescription.amended) ---------

public sealed record AmendPrescriptionCommand(Guid TenantId, Guid PrescriptionId, AmendPrescriptionRequest Request) : ICommand<AmendPrescriptionResult>;

public sealed class AmendPrescriptionValidator : AbstractValidator<AmendPrescriptionCommand>
{
    public AmendPrescriptionValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.PrescriptionId).NotEmpty();
        RuleFor(x => x.Request.MedicationsJson).NotEmpty();
        RuleFor(x => x.Request.AmendmentReason).NotEmpty().MinimumLength(10)
            .WithMessage("An amendment reason (>= 10 chars) is required — amending an issued prescription is auditable.");
    }
}

public sealed class AmendPrescriptionCommandHandler(
    IClinicalRepository clinical,
    IFieldEncryptionService encryption,
    IDrugSafetyScreeningService screening,
    IBookingEventPublisher events,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx,
    IClock clock)
    : ICommandHandler<AmendPrescriptionCommand, AmendPrescriptionResult>
{
    private static readonly HashSet<string> Amendable = new(StringComparer.Ordinal) { "finalized", "delivered" };

    public async Task<AmendPrescriptionResult> Handle(AmendPrescriptionCommand command, CancellationToken ct)
    {
        var req = command.Request;

        // Load the original (RLS + tenant filter also block cross-tenant → 404). We never decrypt or return
        // its PHI here — only its lineage (booking/patient/doctor/status) — so this is not a PHI disclosure.
        var original = (await clinical.GetPrescriptionAsync(command.PrescriptionId, command.TenantId, ct))?.Prescription
            ?? throw new KeyNotFoundException("Prescription not found.");

        if (!Amendable.Contains(original.Status))
            throw new BusinessRuleException(
                $"Prescription cannot be amended (status: {original.Status}). Only an issued (finalized/delivered) prescription can be amended.");

        // Single-winner: flip the original to 'amended' FIRST (conditional UPDATE). If a concurrent amend
        // already won, nothing updates → 409. This + the insert run in the command's UnitOfWork transaction,
        // so a later failure rolls BOTH back (the original is never left superseded with no successor).
        if (!await clinical.MarkPrescriptionSupersededAsync(original.PrescriptionId, command.TenantId, ct))
            throw new ConflictException("Prescription is no longer amendable (it was amended concurrently).");

        var encCtx = new EncryptionContext(ctx.UserId, command.TenantId, "prescription", original.PatientId, ctx.IpAddress);
        var diagnosisEnc = await EncOrNull(req.Diagnosis, PrescriptionFields.Diagnosis, command.TenantId, encCtx, ct);
        var examinationEnc = await EncOrNull(req.Examination, PrescriptionFields.Examination, command.TenantId, encCtx, ct);
        var chiefEnc = await EncOrNull(req.ChiefComplaints, PrescriptionFields.ChiefComplaints, command.TenantId, encCtx, ct);
        var medsEnc = await encryption.EncryptAsync(PrescriptionFields.Medications, command.TenantId, req.MedicationsJson, encCtx, ct);

        var signerUserId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var amendment = Prescription.Amend(
            original.BookingId, original.PatientId, original.DoctorId, command.TenantId,
            chiefEnc, examinationEnc, diagnosisEnc, medsEnc, req.Advice, req.FollowUpInDays,
            original.PrescriptionId, req.AmendmentReason, signerUserId, clock.UtcNow);

        var number = await clinical.AddPrescriptionAsync(amendment, ct);

        // Re-screen the amended medication list (atomic with the amendment, same UoW tx).
        await screening.ScreenPrescriptionAsync(amendment.PrescriptionId, original.PatientId, command.TenantId, req.MedicationsJson, ct);

        await audit.RecordAsync(new AuditEntry(
            "amend", "prescription", amendment.PrescriptionId, number, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Prescription amended (supersedes {original.PrescriptionNumber ?? original.PrescriptionId.ToString()}; PHI encrypted)",
            Purpose: "treatment", LegalBasis: "consent"), ct);

        // Integration event — IDs ONLY, NO PHI.
        await events.PublishAsync("docslot.prescription.amended", command.TenantId, amendment.PrescriptionId, number,
            new
            {
                prescription_id = amendment.PrescriptionId,
                supersedes_prescription_id = original.PrescriptionId,
                patient_id = original.PatientId,
                booking_id = original.BookingId,
            }, ct);

        return new AmendPrescriptionResult(amendment.PrescriptionId, number, original.PrescriptionId);
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

        var detail = await clinical.GetPrescriptionAsync(q.PrescriptionId, q.TenantId, ct)
            ?? throw new KeyNotFoundException("Prescription not found.");   // RLS also blocks cross-tenant
        var p = detail.Prescription;

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
            p.PrescriptionId, p.PrescriptionNumber, p.PatientId, p.DoctorId, detail.DoctorName,
            await DecOrNull(p.ChiefComplaintsEnc, PrescriptionFields.ChiefComplaints, encCtx, ct),
            await DecOrNull(p.ExaminationEnc, PrescriptionFields.Examination, encCtx, ct),
            await DecOrNull(p.DiagnosisEnc, PrescriptionFields.Diagnosis, encCtx, ct),
            await encryption.DecryptAsync(PrescriptionFields.Medications, p.MedicationsEnc, encCtx, ct),
            p.Advice, p.FollowUpInDays, p.Status, p.SupersedesPrescriptionId,
            new DateTimeOffset(DateTime.SpecifyKind(p.CreatedAt, DateTimeKind.Utc)),
            ConsultationJson.ParseVitals(p.Vitals), p.FinalizedByUserId,
            p.FinalizedAt is DateTime f ? new DateTimeOffset(DateTime.SpecifyKind(f, DateTimeKind.Utc)) : null);
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
            r.PrescriptionId, r.PrescriptionNumber, r.DoctorId, r.DoctorName, r.Status,
            new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)))).ToList();
    }
}

// ---- Read a prescription's drug-safety alerts (PHI — purpose-of-use + consent/break-glass gated) ---

public sealed record GetPrescriptionDrugAlertsQuery(Guid TenantId, Guid PrescriptionId, string DeclaredPurpose)
    : IQuery<IReadOnlyList<DrugAlertDto>>;

public sealed class GetPrescriptionDrugAlertsQueryHandler(
    IClinicalRepository clinical,
    IPatientRepository patients,
    IPurposeOfUseWriter purpose,
    IBreakGlassService breakGlass,
    ICurrentUserContext ctx)
    : IQueryHandler<GetPrescriptionDrugAlertsQuery, IReadOnlyList<DrugAlertDto>>
{
    public async Task<IReadOnlyList<DrugAlertDto>> Handle(GetPrescriptionDrugAlertsQuery q, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        if (string.IsNullOrWhiteSpace(q.DeclaredPurpose))
            throw new mediq.Utilities.Exceptions.ValidationException(new Dictionary<string, string[]> { ["X-Purpose-Of-Use"] = ["A purpose-of-use is required to read drug alerts (DPDP)."] });

        var p = (await clinical.GetPrescriptionAsync(q.PrescriptionId, q.TenantId, ct))?.Prescription
            ?? throw new KeyNotFoundException("Prescription not found.");   // RLS also blocks cross-tenant

        // Alerts encode allergy↔prescription PHI → same consent gate as reading the prescription (break-glass aware).
        var patient = await patients.GetByIdAsync(p.PatientId, ct);
        BreakGlassGrant? grant = null;
        if (patient is null || !patient.HasActiveConsent)
        {
            grant = await breakGlass.GetActiveGrantAsync(userId, q.TenantId, p.PatientId, "prescription", p.PrescriptionId, ct);
            if (grant is null)
                throw new ForbiddenException("Patient has no active consent; clinical read refused (DPDP).");
        }

        await purpose.RecordAsync(new PurposeOfUseEntry(
            userId, q.TenantId, "prescription", p.PrescriptionId, q.DeclaredPurpose, null, grant is not null, grant?.Justification), ct);

        var alerts = await clinical.ListDrugAlertsAsync(q.PrescriptionId, q.TenantId, ct);
        return alerts.Select(a => new DrugAlertDto(
            a.AlertId, a.AlertType, a.Severity, a.MedicationName, a.Description, a.Overridden,
            new DateTimeOffset(DateTime.SpecifyKind(a.CreatedAt, DateTimeKind.Utc)))).ToList();
    }
}
