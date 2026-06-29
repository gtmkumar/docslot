using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Domain.Docslot;
using mediq.SharedDataModel.Docslot.Clinical;

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
