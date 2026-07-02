using System.Text;
using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Domain.Docslot;
using mediq.SharedDataModel.Docslot.Clinical;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Docslot.Clinical;

/// <summary>Allowed enumerations for medical-history records (mirror the docslot.patient_medical_history CHECKs).</summary>
public static class MedicalHistoryVocab
{
    public static readonly string[] RecordTypes =
        ["allergy", "chronic_condition", "surgery", "medication", "vaccination", "family_history", "lifestyle"];
    public static readonly string[] Severities = ["mild", "moderate", "severe", "critical"];
}

// ---- Create medical history (encrypts title/description at rest) ----------------------------------

public sealed record CreateMedicalHistoryCommand(Guid TenantId, Guid PatientId, CreateMedicalHistoryRequest Request)
    : ICommand<CreateMedicalHistoryResult>;

public sealed class CreateMedicalHistoryValidator : AbstractValidator<CreateMedicalHistoryCommand>
{
    public CreateMedicalHistoryValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.Request.RecordType).NotEmpty()
            .Must(rt => MedicalHistoryVocab.RecordTypes.Contains(rt))
            .WithMessage("record_type must be one of: " + string.Join(", ", MedicalHistoryVocab.RecordTypes));
        RuleFor(x => x.Request.Title).NotEmpty();
        RuleFor(x => x.Request.Severity)
            .Must(s => s is null || MedicalHistoryVocab.Severities.Contains(s))
            .WithMessage("severity must be null or one of: " + string.Join(", ", MedicalHistoryVocab.Severities));
    }
}

public sealed class CreateMedicalHistoryCommandHandler(
    IClinicalRepository clinical,
    IFieldEncryptionService encryption,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx,
    IClock clock)
    : ICommandHandler<CreateMedicalHistoryCommand, CreateMedicalHistoryResult>
{
    public async Task<CreateMedicalHistoryResult> Handle(CreateMedicalHistoryCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var req = command.Request;
        var encCtx = new EncryptionContext(ctx.UserId, command.TenantId, "medical_history", command.PatientId, ctx.IpAddress);

        var titleEnc = await encryption.EncryptAsync(ClinicalFields.HistoryTitle, command.TenantId, req.Title, encCtx, ct);
        var descEnc = string.IsNullOrEmpty(req.Description)
            ? null
            : await encryption.EncryptAsync(ClinicalFields.HistoryDescription, command.TenantId, req.Description, encCtx, ct);

        var history = MedicalHistory.Create(
            command.PatientId, command.TenantId, req.RecordType, titleEnc, descEnc,
            req.Severity, req.Icd10Code, req.StartedDate, req.EndedDate, req.IsCritical, userId, clock.UtcNow);

        var id = await clinical.AddMedicalHistoryAsync(history, ct);

        // Audit carries the record_type CATEGORY only — never the encrypted title/description (PHI).
        await audit.RecordAsync(new AuditEntry(
            "create", "medical_history", id, req.RecordType, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: "Medical history record added (PHI encrypted)", Purpose: "treatment", LegalBasis: "consent"), ct);

        return new CreateMedicalHistoryResult(id);
    }
}

// ---- Update medical history (re-encrypts title/description) ---------------------------------------

public sealed record UpdateMedicalHistoryCommand(Guid TenantId, Guid HistoryId, UpdateMedicalHistoryRequest Request)
    : ICommand<bool>;

public sealed class UpdateMedicalHistoryValidator : AbstractValidator<UpdateMedicalHistoryCommand>
{
    public UpdateMedicalHistoryValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.HistoryId).NotEmpty();
        RuleFor(x => x.Request.RecordType).NotEmpty()
            .Must(rt => MedicalHistoryVocab.RecordTypes.Contains(rt))
            .WithMessage("record_type must be one of: " + string.Join(", ", MedicalHistoryVocab.RecordTypes));
        RuleFor(x => x.Request.Title).NotEmpty();
        RuleFor(x => x.Request.Severity)
            .Must(s => s is null || MedicalHistoryVocab.Severities.Contains(s))
            .WithMessage("severity must be null or one of: " + string.Join(", ", MedicalHistoryVocab.Severities));
    }
}

public sealed class UpdateMedicalHistoryCommandHandler(
    IClinicalRepository clinical,
    IFieldEncryptionService encryption,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx)
    : ICommandHandler<UpdateMedicalHistoryCommand, bool>
{
    public async Task<bool> Handle(UpdateMedicalHistoryCommand command, CancellationToken ct)
    {
        // Load existing (existence + patient_id for the encryption context). RLS also blocks cross-tenant.
        var existing = await clinical.GetMedicalHistoryAsync(command.HistoryId, command.TenantId, ct)
            ?? throw new KeyNotFoundException("Medical history record not found.");

        var req = command.Request;
        var encCtx = new EncryptionContext(ctx.UserId, command.TenantId, "medical_history", existing.PatientId, ctx.IpAddress);
        var titleEnc = await encryption.EncryptAsync(ClinicalFields.HistoryTitle, command.TenantId, req.Title, encCtx, ct);
        var descEnc = string.IsNullOrEmpty(req.Description)
            ? null
            : await encryption.EncryptAsync(ClinicalFields.HistoryDescription, command.TenantId, req.Description, encCtx, ct);

        var ok = await clinical.UpdateMedicalHistoryAsync(
            command.HistoryId, command.TenantId, req.RecordType, titleEnc, descEnc,
            req.Severity, req.Icd10Code, req.StartedDate, req.EndedDate, req.IsActive, req.IsCritical, ct);

        if (ok)
            await audit.RecordAsync(new AuditEntry(
                "update", "medical_history", command.HistoryId, req.RecordType, ctx.UserId, command.TenantId,
                ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
                ChangeSummary: "Medical history record updated (PHI re-encrypted)", Purpose: "treatment", LegalBasis: "consent"), ct);
        return ok;
    }
}

// ---- Import external history (paper prescription / patient-reported) as UNVERIFIED drafts ----------

/// <summary>The external sources front-desk intake may transcribe. 'clinic' is deliberately EXCLUDED — a clinic
/// record is authored + verified via the single-create path, never imported as an unverified draft.</summary>
public static class MedicalHistoryImportVocab
{
    public static readonly string[] ExternalSources = ["paper_prescription", "patient_reported"];
}

public sealed record ImportMedicalHistoryCommand(Guid TenantId, Guid PatientId, ImportMedicalHistoryRequest Request)
    : ICommand<ImportMedicalHistoryResult>, IRequireIdempotency;

public sealed class ImportMedicalHistoryValidator : AbstractValidator<ImportMedicalHistoryCommand>
{
    // Bound the inline (base64-in-JSON) attachment so one request can't buffer an unbounded body (DoS) — mirrors
    // the lab-report file validator (~28M base64 chars ≈ a 20 MB file; larger scans use the object-store path).
    internal const int MaxBase64Length = 28_000_000;

    public ImportMedicalHistoryValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.Request.Source).NotEmpty()
            .Must(s => MedicalHistoryImportVocab.ExternalSources.Contains(s))
            .WithMessage("source must be one of: " + string.Join(", ", MedicalHistoryImportVocab.ExternalSources) + " (a clinic record is not imported).");
        RuleFor(x => x.Request.Records).NotEmpty().WithMessage("At least one record is required.")
            .Must(r => r is null || r.Count <= 50).WithMessage("An import may contain at most 50 records.");
        RuleForEach(x => x.Request.Records).ChildRules(r =>
        {
            r.RuleFor(x => x.RecordType).NotEmpty()
                .Must(rt => MedicalHistoryVocab.RecordTypes.Contains(rt))
                .WithMessage("record_type must be one of: " + string.Join(", ", MedicalHistoryVocab.RecordTypes));
            r.RuleFor(x => x.Title).NotEmpty();
            r.RuleFor(x => x.Severity)
                .Must(s => s is null || MedicalHistoryVocab.Severities.Contains(s))
                .WithMessage("severity must be null or one of: " + string.Join(", ", MedicalHistoryVocab.Severities));
        });
        When(x => x.Request.Attachment is not null, () =>
        {
            RuleFor(x => x.Request.Attachment!.FileName).NotEmpty();
            RuleFor(x => x.Request.Attachment!.ContentType).NotEmpty();
            RuleFor(x => x.Request.Attachment!.ContentBase64).NotEmpty()
                .Must(s => string.IsNullOrEmpty(s) || s.Length <= MaxBase64Length)
                .WithMessage("Attachment too large for the inline upload (max ~20 MB); use the object-store upload path for larger files.");
        });
    }
}

public sealed class ImportMedicalHistoryCommandHandler(
    IClinicalRepository clinical, IPatientRepository patients, IFieldEncryptionService encryption,
    IBlobStorage blobs, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<ImportMedicalHistoryCommand, ImportMedicalHistoryResult>
{
    public async Task<ImportMedicalHistoryResult> Handle(ImportMedicalHistoryCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var req = command.Request;

        // Patient must belong to the caller's tenant (patient_tenant_links) — same 404 guard as the other
        // patient-scoped history endpoints; patients is cross-tenant so this is the tenant-ownership check.
        if (!await patients.IsLinkedToTenantAsync(command.PatientId, command.TenantId, ct))
            throw new KeyNotFoundException("Patient not found.");

        var batchId = Guid.CreateVersion7();
        var now = clock.UtcNow;
        var encCtx = new EncryptionContext(userId, command.TenantId, "medical_history", command.PatientId, ctx.IpAddress);

        // Encrypt + store the scanned source document (PHI): envelope-encrypt the BYTES (medical_history data_class)
        // BEFORE storage so the blob store only holds ciphertext, then persist the same pointer on every row.
        string? attachmentUrl = null, attachmentFileName = null, attachmentMimeType = null;
        long? attachmentSizeBytes = null;
        if (req.Attachment is { } att)
        {
            byte[] content;
            try { content = Convert.FromBase64String(att.ContentBase64); }
            catch (FormatException)
            {
                throw new mediq.Utilities.Exceptions.ValidationException(
                    new Dictionary<string, string[]> { ["attachment.contentBase64"] = ["Attachment content must be valid base64."] });
            }
            var envelope = await encryption.EncryptBytesAsync(ClinicalFields.HistoryTitle, command.TenantId, content, encCtx, ct);
            var stored = await blobs.PutAsync(command.TenantId, "medical_history", batchId, att.FileName, Encoding.UTF8.GetBytes(envelope), ct);
            attachmentUrl = stored.StorageKey;
            attachmentFileName = att.FileName;
            attachmentMimeType = att.ContentType;
            attachmentSizeBytes = content.LongLength;   // PLAINTEXT size; the stored envelope is larger
        }

        // external_doctor_name reveals a care relationship → encrypt at rest (registry), shared across the batch.
        var externalDoctorEnc = string.IsNullOrWhiteSpace(req.ExternalDoctorName)
            ? null
            : await encryption.EncryptAsync(ClinicalFields.HistoryExternalDoctorName, command.TenantId, req.ExternalDoctorName, encCtx, ct);

        var rows = new List<MedicalHistory>(req.Records.Count);
        foreach (var rec in req.Records)
        {
            var titleEnc = await encryption.EncryptAsync(ClinicalFields.HistoryTitle, command.TenantId, rec.Title, encCtx, ct);
            var descEnc = string.IsNullOrEmpty(rec.Description)
                ? null
                : await encryption.EncryptAsync(ClinicalFields.HistoryDescription, command.TenantId, rec.Description, encCtx, ct);
            rows.Add(MedicalHistory.ImportExternal(
                command.PatientId, command.TenantId, req.Source, rec.RecordType, titleEnc, descEnc,
                rec.Severity, rec.StartedDate, rec.IsCritical ?? false, externalDoctorEnc, req.RecordedDate,
                batchId, attachmentUrl, attachmentFileName, attachmentMimeType, attachmentSizeBytes, userId, now));
        }

        var ids = await clinical.AddMedicalHistoryBatchAsync(rows, ct);

        // ONE audit entry for the batch — record count + source only, NO PHI (titles/doctor stay encrypted).
        await audit.RecordAsync(new AuditEntry(
            "import", "medical_history_batch", batchId, req.Source, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Imported {ids.Count} unverified {req.Source} record(s)" + (attachmentUrl is null ? "" : " with attachment"),
            Purpose: "treatment", LegalBasis: "consent"), ct);

        return new ImportMedicalHistoryResult(batchId, ids);
    }
}

// ---- Verify an imported record (clinician confirms an unverified external draft) ------------------

public sealed record VerifyMedicalHistoryCommand(Guid TenantId, Guid PatientId, Guid HistoryId) : ICommand<bool>;

public sealed class VerifyMedicalHistoryValidator : AbstractValidator<VerifyMedicalHistoryCommand>
{
    public VerifyMedicalHistoryValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.HistoryId).NotEmpty();
    }
}

public sealed class VerifyMedicalHistoryCommandHandler(
    IClinicalRepository clinical, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<VerifyMedicalHistoryCommand, bool>
{
    public async Task<bool> Handle(VerifyMedicalHistoryCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");

        // 404 if missing / cross-tenant (RLS + WHERE tenant_id) or the row is for another patient (no existence leak).
        var existing = await clinical.GetMedicalHistoryAsync(command.HistoryId, command.TenantId, ct);
        if (existing is null || existing.PatientId != command.PatientId)
            throw new KeyNotFoundException("Medical history record not found.");

        // 422 if it isn't verifiable: a clinic row is verified by definition, and a record can't be re-verified.
        if (existing.Source == "clinic")
            throw new BusinessRuleException("A clinic record is verified at creation and cannot be re-verified.");
        if (existing.VerifiedByUserId is not null)
            throw new BusinessRuleException("This record has already been verified.");

        // Single-winner flip (guards a concurrent double-verify → exactly one audit row).
        if (!await clinical.VerifyMedicalHistoryAsync(command.HistoryId, command.TenantId, userId, clock.UtcNow, ct))
            throw new BusinessRuleException("This record has already been verified.");

        await audit.RecordAsync(new AuditEntry(
            "verify", "medical_history", command.HistoryId, existing.RecordType, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: "External medical-history record verified by clinician", Purpose: "treatment", LegalBasis: "consent"), ct);
        return true;
    }
}

// ---- Import attachment download (the scanned document is PHI: consent + purpose + break-glass) -----

public sealed record GetMedicalHistoryAttachmentQuery(Guid TenantId, Guid PatientId, Guid HistoryId, string DeclaredPurpose)
    : IQuery<MedicalHistoryAttachmentDto>;

public sealed class GetMedicalHistoryAttachmentQueryHandler(
    IClinicalRepository clinical, IFieldEncryptionService encryption, IBlobStorage blobs,
    IPatientRepository patients, IPurposeOfUseWriter purpose, IBreakGlassService breakGlass, ICurrentUserContext ctx)
    : IQueryHandler<GetMedicalHistoryAttachmentQuery, MedicalHistoryAttachmentDto>
{
    public async Task<MedicalHistoryAttachmentDto> Handle(GetMedicalHistoryAttachmentQuery q, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        if (string.IsNullOrWhiteSpace(q.DeclaredPurpose))
            throw new mediq.Utilities.Exceptions.ValidationException(new Dictionary<string, string[]> { ["X-Purpose-Of-Use"] = ["A purpose-of-use is required to download a medical-history attachment (DPDP)."] });

        var h = await clinical.GetMedicalHistoryAsync(q.HistoryId, q.TenantId, ct);
        if (h is null || h.PatientId != q.PatientId)
            throw new KeyNotFoundException("Medical history record not found.");

        // Same consent gate (+ break-glass) as reading a lab-report file: the scanned document IS PHI. A
        // record-scoped OR patient-wide medical_history grant unlocks a consent-denied read.
        var patient = await patients.GetByIdAsync(h.PatientId, ct);
        BreakGlassGrant? grant = null;
        if (patient is null || !patient.HasActiveConsent)
        {
            grant = await breakGlass.GetActiveGrantAsync(userId, q.TenantId, h.PatientId, "medical_history", h.HistoryId, ct);
            if (grant is null)
                throw new ForbiddenException("Patient has no active consent; clinical read refused (DPDP).");
        }

        await purpose.RecordAsync(new PurposeOfUseEntry(
            userId, q.TenantId, "medical_history", h.HistoryId, q.DeclaredPurpose, null, grant is not null, grant?.Justification), ct);

        if (string.IsNullOrEmpty(h.AttachmentUrl))
            throw new KeyNotFoundException("No attachment on this medical-history record.");
        var stored = await blobs.GetAsync(h.AttachmentUrl, q.TenantId, ct)
            ?? throw new KeyNotFoundException("Medical-history attachment not found in storage.");

        var encCtx = new EncryptionContext(userId, q.TenantId, "medical_history", h.PatientId, ctx.IpAddress);
        var content = await encryption.DecryptBytesAsync(ClinicalFields.HistoryTitle, Encoding.UTF8.GetString(stored), encCtx, ct);

        return new MedicalHistoryAttachmentDto(h.HistoryId, h.AttachmentFileName ?? "attachment",
            h.AttachmentMimeType ?? "application/octet-stream", content);
    }
}
