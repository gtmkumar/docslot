using System.Text;
using System.Text.Json;
using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Domain.Docslot;
using mediq.SharedDataModel.Docslot.Clinical;
using mediq.Utilities.Exceptions;
using Microsoft.Extensions.Logging;

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
    IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock,
    ILogger<UploadLabReportCommandHandler> logger)
    : ICommandHandler<UploadLabReportCommand, UploadLabReportResult>
{
    public async Task<UploadLabReportResult> Handle(UploadLabReportCommand command, CancellationToken ct)
    {
        var req = command.Request;

        // Tenant-ownership guard: test_id is an OPTIONAL, tenant-blind FK and docslot.test_catalog has no RLS,
        // so when supplied it must belong to the caller's tenant — otherwise a lab report in THIS tenant could
        // reference ANOTHER tenant's test_id (#71).
        if (req.TestId is { } testId && !await clinical.TestBelongsToTenantAsync(testId, command.TenantId, ct))
        {
            // Security signal (survives the rollback the throw triggers; non-transactional; IDs only, no PHI).
            logger.LogWarning(
                "SECURITY: lab-report-upload rejected — test {TestId} is not a valid active test in tenant {TenantId} (user {UserId})",
                testId, command.TenantId, ctx.UserId);
            throw new mediq.Utilities.Exceptions.ValidationException(
                new Dictionary<string, string[]> { ["testId"] = ["The referenced test was not found for this tenant."] });
        }

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

        var detail = await clinical.GetLabReportAsync(q.ReportId, q.TenantId, ct)
            ?? throw new KeyNotFoundException("Lab report not found.");
        var r = detail.Report;
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

        return new LabReportDto(r.ReportId, r.ReportNumber, r.PatientId, r.TestId, detail.TestName, r.FileName,
            resultsJson, r.Status, r.HasCriticalFindings, new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)));
    }
}

// ---- Lab report file: store the PHI artifact (encrypted-at-rest blob) ------------------------------

public sealed record SetLabReportFileCommand(Guid TenantId, Guid ReportId, SetLabReportFileRequest Request) : ICommand<SetLabReportFileResult>;

public sealed class SetLabReportFileValidator : AbstractValidator<SetLabReportFileCommand>
{
    // Bound the inline (base64-in-JSON) upload so a single request can't buffer an unbounded body (DoS).
    // ~28M base64 chars ≈ a 20 MB file; larger files use the object-store/presigned path in prod.
    internal const int MaxBase64Length = 28_000_000;

    public SetLabReportFileValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.ReportId).NotEmpty();
        RuleFor(x => x.Request.FileName).NotEmpty();
        RuleFor(x => x.Request.ContentType).NotEmpty();
        RuleFor(x => x.Request.ContentBase64).NotEmpty()
            .Must(s => string.IsNullOrEmpty(s) || s.Length <= MaxBase64Length)
            .WithMessage("File too large for the inline upload (max ~20 MB); use the object-store upload path for larger files.");
    }
}

public sealed class SetLabReportFileCommandHandler(
    IClinicalRepository clinical, IFieldEncryptionService encryption, IBlobStorage blobs,
    IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<SetLabReportFileCommand, SetLabReportFileResult>
{
    public async Task<SetLabReportFileResult> Handle(SetLabReportFileCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var report = (await clinical.GetLabReportAsync(command.ReportId, command.TenantId, ct))?.Report
            ?? throw new KeyNotFoundException("Lab report not found.");   // RLS/tenant filter blocks cross-tenant

        byte[] content;
        try { content = Convert.FromBase64String(req.ContentBase64); }
        catch (FormatException)
        {
            throw new mediq.Utilities.Exceptions.ValidationException(new Dictionary<string, string[]> { ["contentBase64"] = ["File content must be valid base64."] });
        }

        // The artifact is PHI → envelope-encrypt the BYTES (medical_history data_class) BEFORE storage, so the
        // blob store only holds ciphertext and DPDP key destruction renders it unrecoverable. file_size_bytes
        // records the PLAINTEXT size; the stored envelope is larger.
        var encCtx = new EncryptionContext(ctx.UserId, command.TenantId, "lab_report", report.PatientId, ctx.IpAddress);
        var envelope = await encryption.EncryptBytesAsync(ClinicalFields.ReportResults, command.TenantId, content, encCtx, ct);
        var stored = await blobs.PutAsync(command.TenantId, "lab_report", report.ReportId, req.FileName, Encoding.UTF8.GetBytes(envelope), ct);

        await clinical.SetLabReportFileAsync(report.ReportId, command.TenantId, stored.StorageKey, req.FileName,
            content.LongLength, req.ContentType, ctx.UserId, clock.UtcNow, ct);

        await audit.RecordAsync(new AuditEntry(
            "upload_file", "lab_report", report.ReportId, report.ReportNumber, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Lab-report file stored (encrypted; {content.LongLength} bytes)"), ct);

        return new SetLabReportFileResult(report.ReportId, content.LongLength);
    }
}

// ---- Lab report file download (decrypts; purpose + consent + break-glass — the file is PHI) ---------

public sealed record GetLabReportFileQuery(Guid TenantId, Guid ReportId, string DeclaredPurpose) : IQuery<LabReportFileDto>;

public sealed class GetLabReportFileQueryHandler(
    IClinicalRepository clinical, IFieldEncryptionService encryption, IBlobStorage blobs,
    IPatientRepository patients, IPurposeOfUseWriter purpose, IBreakGlassService breakGlass, ICurrentUserContext ctx)
    : IQueryHandler<GetLabReportFileQuery, LabReportFileDto>
{
    public async Task<LabReportFileDto> Handle(GetLabReportFileQuery q, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        if (string.IsNullOrWhiteSpace(q.DeclaredPurpose))
            throw new mediq.Utilities.Exceptions.ValidationException(new Dictionary<string, string[]> { ["X-Purpose-Of-Use"] = ["A purpose-of-use is required to download a lab report (DPDP)."] });

        var r = (await clinical.GetLabReportAsync(q.ReportId, q.TenantId, ct))?.Report
            ?? throw new KeyNotFoundException("Lab report not found.");

        // Same consent gate (+ break-glass) as reading the structured results: the file IS PHI.
        var patient = await patients.GetByIdAsync(r.PatientId, ct);
        BreakGlassGrant? grant = null;
        if (patient is null || !patient.HasActiveConsent)
        {
            grant = await breakGlass.GetActiveGrantAsync(userId, q.TenantId, r.PatientId, "lab_report", r.ReportId, ct);
            if (grant is null)
                throw new ForbiddenException("Patient has no active consent; clinical read refused (DPDP).");
        }

        await purpose.RecordAsync(new PurposeOfUseEntry(
            userId, q.TenantId, "lab_report", r.ReportId, q.DeclaredPurpose, null, grant is not null, grant?.Justification), ct);

        if (string.IsNullOrEmpty(r.FileUrl))
            throw new KeyNotFoundException("No file attached to this lab report.");
        var stored = await blobs.GetAsync(r.FileUrl, q.TenantId, ct)
            ?? throw new KeyNotFoundException("Lab-report file not found in storage.");

        var encCtx = new EncryptionContext(userId, q.TenantId, "lab_report", r.PatientId, ctx.IpAddress);
        var content = await encryption.DecryptBytesAsync(ClinicalFields.ReportResults, Encoding.UTF8.GetString(stored), encCtx, ct);

        return new LabReportFileDto(r.ReportId, r.FileName ?? "lab-report", r.FileMimeType ?? "application/octet-stream", content);
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
            result.Add(new MedicalHistoryDto(h.HistoryId, h.RecordType, title, desc,
                h.Severity, h.Icd10Code, h.StartedDate, h.EndedDate,
                h.IsActive, h.IsCritical, new DateTimeOffset(DateTime.SpecifyKind(h.AddedAt, DateTimeKind.Utc))));
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
        // Decrypt the FHIR bundle TRANSIENTLY (server-side only) to derive a resource count. The plaintext bundle
        // is PHI and is NEVER serialized to the client (issue #54) — only the integer count leaves this method.
        var fhirBundleJson = await encryption.DecryptAsync(ClinicalFields.FhirBundle, r.FhirBundleEnc, encCtx, ct);
        return new AbdmRecordDto(r.RecordId, r.PatientId, r.AbhaNumber, r.RecordType,
            CountFhirResources(fhirBundleJson), r.IsLinkedToPhr,
            r.CareContextId, new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)));
    }

    /// <summary>
    /// Counts the FHIR resources in a decrypted bundle WITHOUT exposing any of its content. A FHIR Bundle carries
    /// an <c>entry</c> array (one element per resource) → count = its length; a single bare resource (an object
    /// with a <c>resourceType</c> but no entry array) counts as 1; anything else / malformed JSON → 0. Parsed
    /// defensively so a bad payload can never leak the bundle or throw.
    /// </summary>
    private static int CountFhirResources(string? bundleJson)
    {
        if (string.IsNullOrWhiteSpace(bundleJson)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(bundleJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return 0;
            if (root.TryGetProperty("entry", out var entry) && entry.ValueKind == JsonValueKind.Array)
                return entry.GetArrayLength();
            return root.TryGetProperty("resourceType", out _) ? 1 : 0;
        }
        catch (JsonException)
        {
            return 0;   // malformed → 0 (never leak or throw)
        }
    }
}

// ---- ABDM care-context LINK (publish a stored record to the national network; consent-REQUIRED) --------

/// <summary>
/// Publishes a stored ABDM record's care context to the national ABDM network via <see cref="IAbdmGateway"/>
/// (HIP data push). ISelfManagedTransaction so the gateway call runs OUTSIDE any DB transaction (no row lock
/// held across network I/O — the payout-rail pattern): phase 1 load + ABDM-consent gate (own committed scope),
/// phase 2 the gateway call, phase 3 the single-winner linkage flip + audit + event (own committed scope).
/// </summary>
public sealed record LinkAbdmRecordCommand(Guid TenantId, Guid RecordId) : ICommand<LinkAbdmRecordResult>, ISelfManagedTransaction;

public sealed class LinkAbdmRecordValidator : AbstractValidator<LinkAbdmRecordCommand>
{
    public LinkAbdmRecordValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.RecordId).NotEmpty();
    }
}

public sealed class LinkAbdmRecordCommandHandler(
    IClinicalRepository clinical, IAbdmConsentService consent, IAbdmGateway gateway,
    IBookingEventPublisher events, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock, IUnitOfWork uow)
    : ICommandHandler<LinkAbdmRecordCommand, LinkAbdmRecordResult>
{
    public async Task<LinkAbdmRecordResult> Handle(LinkAbdmRecordCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");

        // ── Phase 1 — load + ABDM-consent gate in an own committed scope; release before the gateway call.
        AbdmHealthRecord record;
        await using (var load = await uow.BeginTenantScopeAsync(command.TenantId, ct))
        {
            record = await clinical.GetAbdmRecordAsync(command.RecordId, command.TenantId, ct)
                ?? throw new KeyNotFoundException("ABDM record not found.");   // RLS also blocks cross-tenant

            // Linking DISCLOSES the care context to the national ABDM network → an active ABDM consent is MANDATORY.
            if (await consent.GetActiveConsentIdAsync(record.PatientId, command.TenantId, ct) is null)
                throw new ForbiddenException("No active ABDM consent for this patient/tenant; ABDM link denied.");

            if (record.IsLinkedToPhr)   // already published → idempotent
            {
                await load.CommitAsync(ct);
                return new LinkAbdmRecordResult(record.RecordId, true, record.CareContextId, gateway.ProviderName);
            }
            await load.CommitAsync(ct);
        }

        // ── Phase 2 — publish to the ABDM network OUTSIDE any DB tx (no lock held across the network I/O).
        var result = await gateway.LinkCareContextAsync(
            new AbdmLinkRequest(command.TenantId, record.PatientId, record.RecordId, record.AbhaNumber, record.RecordType, record.ConsentId), ct);

        if (!result.Linked)
            throw new BusinessRuleException($"ABDM link was not completed: {result.FailureReason ?? "gateway declined"}");

        // ── Phase 3 — persist the linkage in a fresh committed tx; the conditional UPDATE is the single-winner gate.
        await using (var settle = await uow.BeginTenantScopeAsync(command.TenantId, ct))
        {
            // false → a concurrent link already won (record already linked). Idempotent: do NOT write a second
            // audit row or re-publish the event — return the persisted care context.
            if (!await clinical.MarkAbdmRecordLinkedAsync(record.RecordId, command.TenantId, result.CareContextId, clock.UtcNow, ct))
            {
                var current = await clinical.GetAbdmRecordAsync(record.RecordId, command.TenantId, ct);
                await settle.CommitAsync(ct);
                return new LinkAbdmRecordResult(record.RecordId, true, current?.CareContextId ?? result.CareContextId, gateway.ProviderName);
            }

            // Single winner only — exactly one audit row + one event per real linkage.
            await audit.RecordAsync(new AuditEntry(
                "link", "abdm_record", record.RecordId, null, userId, command.TenantId,
                ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
                ChangeSummary: $"ABDM care-context linked via {gateway.ProviderName} (care_context_id set)",
                Purpose: "treatment", LegalBasis: "consent"), ct);

            // Integration event — IDs ONLY, NO PHI / no ABDM identifiers.
            await events.PublishAsync("docslot.abdm.record.linked", command.TenantId, record.RecordId, null,
                new { record_id = record.RecordId, patient_id = record.PatientId, booking_id = record.BookingId }, ct);

            await settle.CommitAsync(ct);
        }

        return new LinkAbdmRecordResult(record.RecordId, true, result.CareContextId, gateway.ProviderName);
    }
}
