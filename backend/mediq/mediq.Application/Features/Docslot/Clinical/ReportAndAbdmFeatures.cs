using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Domain.Docslot;
using mediq.SharedDataModel.Docslot.Clinical;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Docslot.Clinical;

internal static class ClinicalFields
{
    public static readonly FieldRef ReportResults = new("docslot", "lab_reports", "structured_results");
    public static readonly FieldRef HistoryTitle = new("docslot", "patient_medical_history", "title");
    public static readonly FieldRef HistoryDescription = new("docslot", "patient_medical_history", "description");
    public static readonly FieldRef FhirBundle = new("docslot", "abdm_health_records", "fhir_bundle");
}

// ---- Lab report upload (encrypts structured results, emits docslot.report.delivered on deliver) ----

public sealed record UploadLabReportCommand(Guid TenantId, UploadLabReportRequest Request) : ICommand<UploadLabReportResult>;

public sealed class UploadLabReportValidator : AbstractValidator<UploadLabReportCommand>
{
    public UploadLabReportValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.BookingId).NotEmpty();
        RuleFor(x => x.Request.PatientId).NotEmpty();
    }
}

public sealed class UploadLabReportCommandHandler(
    IClinicalRepository clinical, IFieldEncryptionService encryption, IBookingEventPublisher events,
    IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<UploadLabReportCommand, UploadLabReportResult>
{
    public async Task<UploadLabReportResult> Handle(UploadLabReportCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var encCtx = new EncryptionContext(ctx.UserId, command.TenantId, "lab_report", req.PatientId, ctx.IpAddress);
        var resultsEnc = string.IsNullOrEmpty(req.StructuredResultsJson)
            ? null
            : await encryption.EncryptAsync(ClinicalFields.ReportResults, command.TenantId, req.StructuredResultsJson, encCtx, ct);

        var report = LabReport.Upload(
            req.BookingId, req.PatientId, command.TenantId, req.TestId, req.FileName,
            resultsEnc, req.HasCriticalFindings, clock.UtcNow);

        var number = await clinical.AddLabReportAsync(report, ct);

        await audit.RecordAsync(new AuditEntry(
            "upload", "lab_report", report.ReportId, number, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: "Lab report uploaded (PHI encrypted)"), ct);

        // Integration event — IDs ONLY, NO PHI.
        await events.PublishAsync("docslot.report.delivered", command.TenantId, report.ReportId, number,
            new { report_id = report.ReportId, patient_id = req.PatientId, booking_id = req.BookingId }, ct);

        return new UploadLabReportResult(report.ReportId, number);
    }
}

// ---- Read lab report (decrypts; purpose + consent) ------------------------------------------------

public sealed record GetLabReportQuery(Guid TenantId, Guid ReportId, string DeclaredPurpose) : IQuery<LabReportDto>;

public sealed class GetLabReportQueryHandler(
    IClinicalRepository clinical, IFieldEncryptionService encryption, IPatientRepository patients,
    IPurposeOfUseWriter purpose, IBreakGlassService breakGlass, ICurrentUserContext ctx)
    : IQueryHandler<GetLabReportQuery, LabReportDto>
{
    public async Task<LabReportDto> Handle(GetLabReportQuery q, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        if (string.IsNullOrWhiteSpace(q.DeclaredPurpose))
            throw new mediq.Utilities.Exceptions.ValidationException(new Dictionary<string, string[]> { ["X-Purpose-Of-Use"] = ["A purpose-of-use is required to read a lab report (DPDP)."] });

        var r = await clinical.GetLabReportAsync(q.ReportId, q.TenantId, ct)
            ?? throw new KeyNotFoundException("Lab report not found.");
        // Consent gate with break-glass override (FR-MED-03): a specific-report grant or a patient-wide grant unlocks.
        var patient = await patients.GetByIdAsync(r.PatientId, ct);
        BreakGlassGrant? grant = null;
        if (patient is null || !patient.HasActiveConsent)
        {
            grant = await breakGlass.GetActiveGrantAsync(userId, q.TenantId, r.PatientId, "lab_report", r.ReportId, ct);
            if (grant is null)
                throw new ForbiddenException("Patient has no active consent; clinical read refused (DPDP).");
        }

        await purpose.RecordAsync(new PurposeOfUseEntry(userId, q.TenantId, "lab_report", r.ReportId, q.DeclaredPurpose, null, grant is not null, grant?.Justification), ct);

        var encCtx = new EncryptionContext(userId, q.TenantId, "lab_report", r.PatientId, ctx.IpAddress);
        var resultsJson = string.IsNullOrEmpty(r.StructuredResultsEnc)
            ? null
            : await encryption.DecryptAsync(ClinicalFields.ReportResults, r.StructuredResultsEnc, encCtx, ct);

        return new LabReportDto(r.ReportId, r.ReportNumber, r.PatientId, r.TestId, r.FileName,
            resultsJson, r.Status, r.HasCriticalFindings, new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)));
    }
}

// ---- Lab report list (headers only; purpose + consent; NO decryption) ----------------------------

public sealed record ListLabReportsQuery(Guid TenantId, Guid PatientId, string DeclaredPurpose) : IQuery<IReadOnlyList<LabReportListItemDto>>;

public sealed class ListLabReportsQueryHandler(
    IClinicalRepository clinical, IPatientRepository patients, IPurposeOfUseWriter purpose,
    IBreakGlassService breakGlass, ICurrentUserContext ctx)
    : IQueryHandler<ListLabReportsQuery, IReadOnlyList<LabReportListItemDto>>
{
    public async Task<IReadOnlyList<LabReportListItemDto>> Handle(ListLabReportsQuery q, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        if (string.IsNullOrWhiteSpace(q.DeclaredPurpose))
            throw new mediq.Utilities.Exceptions.ValidationException(new Dictionary<string, string[]> { ["X-Purpose-Of-Use"] = ["A purpose-of-use is required to list lab reports (DPDP)."] });

        // A LIST exposes every report's existence → it requires a PATIENT-WIDE grant (resource_id NULL);
        // a grant scoped to a single report does NOT unlock the list.
        var patient = await patients.GetByIdAsync(q.PatientId, ct);
        BreakGlassGrant? grant = null;
        if (patient is null || !patient.HasActiveConsent)
        {
            grant = await breakGlass.GetActiveGrantAsync(userId, q.TenantId, q.PatientId, "lab_report", null, ct);
            if (grant is null)
                throw new ForbiddenException("Patient has no active consent; clinical read refused (DPDP).");
        }

        var rows = await clinical.ListLabReportsAsync(q.TenantId, q.PatientId, ct);
        await purpose.RecordAsync(new PurposeOfUseEntry(userId, q.TenantId, "lab_report", q.PatientId, q.DeclaredPurpose, null, grant is not null, grant?.Justification), ct);

        return rows.Select(r => new LabReportListItemDto(
            r.ReportId, r.ReportNumber, r.TestName, r.Status, r.HasCriticalFindings,
            new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)))).ToList();
    }
}

// ---- Lab report deliver (status→delivered; emits docslot.report.delivered) -----------------------

public sealed record DeliverLabReportCommand(Guid TenantId, Guid ReportId) : ICommand<DeliverLabReportResult>;

public sealed class DeliverLabReportValidator : AbstractValidator<DeliverLabReportCommand>
{
    public DeliverLabReportValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.ReportId).NotEmpty();
    }
}

public sealed class DeliverLabReportCommandHandler(
    IClinicalRepository clinical, IBookingEventPublisher events, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<DeliverLabReportCommand, DeliverLabReportResult>
{
    public async Task<DeliverLabReportResult> Handle(DeliverLabReportCommand command, CancellationToken ct)
    {
        var updated = await clinical.DeliverLabReportAsync(command.ReportId, command.TenantId, ct)
            ?? throw new KeyNotFoundException("Lab report not found.");

        await audit.RecordAsync(new AuditEntry(
            "deliver", "lab_report", command.ReportId, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: "Lab report marked delivered"), ct);

        // Integration event — IDs ONLY, NO PHI.
        await events.PublishAsync("docslot.report.delivered", command.TenantId, command.ReportId, null,
            new { report_id = command.ReportId }, ct);

        return new DeliverLabReportResult(command.ReportId, updated.Status,
            updated.DeliveredAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(updated.DeliveredAt.Value, DateTimeKind.Utc)));
    }
}

// ---- ABDM record list (headers only; consent-REQUIRED) -------------------------------------------

public sealed record ListAbdmRecordsQuery(Guid TenantId, Guid PatientId, string DeclaredPurpose) : IQuery<IReadOnlyList<AbdmRecordListItemDto>>;

public sealed class ListAbdmRecordsQueryHandler(
    IClinicalRepository clinical, IAbdmConsentService consent, IPurposeOfUseWriter purpose, ICurrentUserContext ctx)
    : IQueryHandler<ListAbdmRecordsQuery, IReadOnlyList<AbdmRecordListItemDto>>
{
    public async Task<IReadOnlyList<AbdmRecordListItemDto>> Handle(ListAbdmRecordsQuery q, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        if (string.IsNullOrWhiteSpace(q.DeclaredPurpose))
            throw new mediq.Utilities.Exceptions.ValidationException(new Dictionary<string, string[]> { ["X-Purpose-Of-Use"] = ["A purpose-of-use is required to list ABDM records (DPDP)."] });

        // ABDM list is consent-REQUIRED (same gate as the detail read).
        if (!await consent.HasActiveConsentAsync(q.PatientId, q.TenantId, ct))
            throw new ForbiddenException("No active ABDM consent for this patient/tenant; ABDM list denied.");

        var rows = await clinical.ListAbdmRecordsAsync(q.TenantId, q.PatientId, ct);
        await purpose.RecordAsync(new PurposeOfUseEntry(userId, q.TenantId, "abdm_record", q.PatientId, q.DeclaredPurpose, null, false, null), ct);

        return rows.Select(r => new AbdmRecordListItemDto(
            r.RecordId, r.RecordType, r.AbhaNumber, r.IsLinkedToPhr,
            new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)))).ToList();
    }
}

// ---- Patient consent context (no PHI beyond masked phone) -----------------------------------------

public sealed record GetPatientConsentQuery(Guid TenantId, Guid PatientId) : IQuery<PatientConsentDto>;

public sealed class GetPatientConsentQueryHandler(IClinicalRepository clinical)
    : IQueryHandler<GetPatientConsentQuery, PatientConsentDto>
{
    public async Task<PatientConsentDto> Handle(GetPatientConsentQuery q, CancellationToken ct)
    {
        var c = await clinical.GetConsentContextAsync(q.TenantId, q.PatientId, ct)
            ?? throw new KeyNotFoundException("Patient not found.");
        return new PatientConsentDto(
            c.PatientId, c.MaskedPhone,
            c.ClinicalConsentActive ? "granted" : "revoked",
            c.AbdmConsentActive ? "granted" : "revoked",
            c.AbdmConsentExpiresAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(c.AbdmConsentExpiresAt.Value, DateTimeKind.Utc)));
    }
}

// ---- Medical history read (decrypts title/description; purpose + consent) -------------------------

public sealed record ListMedicalHistoryQuery(Guid TenantId, Guid PatientId, string DeclaredPurpose) : IQuery<IReadOnlyList<MedicalHistoryDto>>;

public sealed class ListMedicalHistoryQueryHandler(
    IClinicalRepository clinical, IFieldEncryptionService encryption, IPatientRepository patients,
    IPurposeOfUseWriter purpose, IBreakGlassService breakGlass, ICurrentUserContext ctx)
    : IQueryHandler<ListMedicalHistoryQuery, IReadOnlyList<MedicalHistoryDto>>
{
    public async Task<IReadOnlyList<MedicalHistoryDto>> Handle(ListMedicalHistoryQuery q, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        if (string.IsNullOrWhiteSpace(q.DeclaredPurpose))
            throw new mediq.Utilities.Exceptions.ValidationException(new Dictionary<string, string[]> { ["X-Purpose-Of-Use"] = ["A purpose-of-use is required to read medical history (DPDP)."] });

        // Patient-wide history list → requires a patient-wide medical_history grant (resource_id NULL).
        var patient = await patients.GetByIdAsync(q.PatientId, ct);
        BreakGlassGrant? grant = null;
        if (patient is null || !patient.HasActiveConsent)
        {
            grant = await breakGlass.GetActiveGrantAsync(userId, q.TenantId, q.PatientId, "medical_history", null, ct);
            if (grant is null)
                throw new ForbiddenException("Patient has no active consent; clinical read refused (DPDP).");
        }

        var rows = await clinical.ListMedicalHistoryAsync(q.TenantId, q.PatientId, ct);
        await purpose.RecordAsync(new PurposeOfUseEntry(userId, q.TenantId, "medical_history", q.PatientId, q.DeclaredPurpose, null, grant is not null, grant?.Justification), ct);

        var encCtx = new EncryptionContext(userId, q.TenantId, "medical_history", q.PatientId, ctx.IpAddress);
        var result = new List<MedicalHistoryDto>(rows.Count);
        foreach (var h in rows)
        {
            var title = await encryption.DecryptAsync(ClinicalFields.HistoryTitle, h.TitleEnc, encCtx, ct);
            var desc = string.IsNullOrEmpty(h.DescriptionEnc) ? null : await encryption.DecryptAsync(ClinicalFields.HistoryDescription, h.DescriptionEnc, encCtx, ct);
            result.Add(new MedicalHistoryDto(h.HistoryId, h.RecordType, title, desc, h.IsActive, h.IsCritical,
                new DateTimeOffset(DateTime.SpecifyKind(h.AddedAt, DateTimeKind.Utc))));
        }
        return result;
    }
}

// ---- ABDM push (consent-REQUIRED) ----------------------------------------------------------------

public sealed record PushAbdmRecordCommand(Guid TenantId, PushAbdmRecordRequest Request) : ICommand<PushAbdmRecordResult>;

public sealed class PushAbdmRecordValidator : AbstractValidator<PushAbdmRecordCommand>
{
    public PushAbdmRecordValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.PatientId).NotEmpty();
        RuleFor(x => x.Request.AbhaNumber).NotEmpty();
        RuleFor(x => x.Request.FhirBundleJson).NotEmpty();
    }
}

public sealed class PushAbdmRecordCommandHandler(
    IClinicalRepository clinical, IFieldEncryptionService encryption, IAbdmConsentService consent,
    IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<PushAbdmRecordCommand, PushAbdmRecordResult>
{
    public async Task<PushAbdmRecordResult> Handle(PushAbdmRecordCommand command, CancellationToken ct)
    {
        var req = command.Request;
        // ABDM consent is MANDATORY (deny otherwise).
        var consentId = await consent.GetActiveConsentIdAsync(req.PatientId, command.TenantId, ct);
        if (consentId is null)
            throw new ForbiddenException("No active ABDM consent for this patient/tenant; ABDM push denied.");

        var encCtx = new EncryptionContext(ctx.UserId, command.TenantId, "abdm_record", req.PatientId, ctx.IpAddress);
        var fhirEnc = await encryption.EncryptAsync(ClinicalFields.FhirBundle, command.TenantId, req.FhirBundleJson, encCtx, ct);

        var record = AbdmHealthRecord.Push(
            req.PatientId, command.TenantId, req.BookingId, req.AbhaNumber, req.RecordType, fhirEnc, consentId.ToString(), clock.UtcNow);
        await clinical.AddAbdmRecordAsync(record, ct);

        await audit.RecordAsync(new AuditEntry(
            "push", "abdm_record", record.RecordId, req.AbhaNumber, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: "ABDM record pushed (consent verified, PHI encrypted)",
            Purpose: "treatment", LegalBasis: "consent"), ct);

        return new PushAbdmRecordResult(record.RecordId);
    }
}

// ---- ABDM read (consent-REQUIRED + purpose) ------------------------------------------------------

public sealed record GetAbdmRecordQuery(Guid TenantId, Guid RecordId, string DeclaredPurpose) : IQuery<AbdmRecordDto>;

public sealed class GetAbdmRecordQueryHandler(
    IClinicalRepository clinical, IFieldEncryptionService encryption, IAbdmConsentService consent,
    IPurposeOfUseWriter purpose, ICurrentUserContext ctx)
    : IQueryHandler<GetAbdmRecordQuery, AbdmRecordDto>
{
    public async Task<AbdmRecordDto> Handle(GetAbdmRecordQuery q, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        if (string.IsNullOrWhiteSpace(q.DeclaredPurpose))
            throw new mediq.Utilities.Exceptions.ValidationException(new Dictionary<string, string[]> { ["X-Purpose-Of-Use"] = ["A purpose-of-use is required to read an ABDM record (DPDP)."] });

        var r = await clinical.GetAbdmRecordAsync(q.RecordId, q.TenantId, ct)
            ?? throw new KeyNotFoundException("ABDM record not found.");
        if (!await consent.HasActiveConsentAsync(r.PatientId, q.TenantId, ct))
            throw new ForbiddenException("No active ABDM consent for this patient/tenant; ABDM read denied.");

        await purpose.RecordAsync(new PurposeOfUseEntry(userId, q.TenantId, "abdm_record", r.RecordId, q.DeclaredPurpose, null, false, null), ct);

        var encCtx = new EncryptionContext(userId, q.TenantId, "abdm_record", r.PatientId, ctx.IpAddress);
        var fhir = await encryption.DecryptAsync(ClinicalFields.FhirBundle, r.FhirBundleEnc, encCtx, ct);
        return new AbdmRecordDto(r.RecordId, r.PatientId, r.AbhaNumber, r.RecordType, fhir, r.IsLinkedToPhr,
            new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)));
    }
}
