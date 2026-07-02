using System.Text.Json;
using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Domain.Docslot;
using mediq.SharedDataModel.Docslot.Clinical;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Docslot.Clinical;

/// <summary>
/// Consultation composer (Phase A of docs/PRESCRIPTION_CONSULTATION_PLAN.md): one record, two roles, one
/// signing transition (draft → finalized). Reuses the encrypted-at-rest clinical fields exactly as
/// IssuePrescription does (chief_complaints/examination/diagnosis/medications), while vitals + investigations
/// are unencrypted standard PHI carried as raw JSON. All three commands are gated on
/// <c>docslot.prescription.create</c> at the controller in Phase A (Phase B relaxes create/patch to
/// <c>docslot.prescription.draft</c>).
/// </summary>
internal static class ConsultationJson
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Parses the vitals JSONB object text (defaults to an all-null <see cref="VitalsDto"/>).</summary>
    public static VitalsDto ParseVitals(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new VitalsDto();
        try { return JsonSerializer.Deserialize<VitalsDto>(json, Web) ?? new VitalsDto(); }
        catch (JsonException) { return new VitalsDto(); }
    }

    public static string SerializeVitals(VitalsDto vitals) => JsonSerializer.Serialize(vitals, Web);

    /// <summary>Parses the investigations JSONB array text (defaults to empty).</summary>
    public static string[] ParseInvestigations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json, Web) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string SerializeInvestigations(string[] investigations) => JsonSerializer.Serialize(investigations, Web);
}

// ---- Create/get the draft for a booking (idempotent per booking; returns DECRYPTED PHI) -----------

/// <summary>Get-or-create the draft prescription for a booking. Returns decrypted PHI → IDoNotCacheResponse
/// (the idempotency store is plaintext) + consent/purpose gated exactly like GetPrescription.</summary>
public sealed record CreateConsultationDraftCommand(Guid TenantId, Guid BookingId, string DeclaredPurpose)
    : ICommand<ConsultationDraftDto>, IDoNotCacheResponse;

public sealed class CreateConsultationDraftValidator : AbstractValidator<CreateConsultationDraftCommand>
{
    public CreateConsultationDraftValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.BookingId).NotEmpty();
    }
}

public sealed class CreateConsultationDraftCommandHandler(
    IBookingRepository bookings,
    IClinicalRepository clinical,
    IFieldEncryptionService encryption,
    IPatientRepository patients,
    IPurposeOfUseWriter purpose,
    IBreakGlassService breakGlass,
    IUnitOfWork uow,
    ICurrentUserContext ctx,
    IClock clock)
    : ICommandHandler<CreateConsultationDraftCommand, ConsultationDraftDto>
{
    public async Task<ConsultationDraftDto> Handle(CreateConsultationDraftCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        if (string.IsNullOrWhiteSpace(command.DeclaredPurpose))
            throw new mediq.Utilities.Exceptions.ValidationException(
                new Dictionary<string, string[]> { ["X-Purpose-Of-Use"] = ["A purpose-of-use is required to open a consultation (DPDP)."] });

        // The booking is the source of truth for patient/doctor/tenant. RLS + tenant filter → 404 cross-tenant.
        var booking = await bookings.GetByIdAsync(command.BookingId, command.TenantId, ct)
            ?? throw new KeyNotFoundException("Booking not found.");

        // Consent gate (break-glass aware), same as GetPrescription. A new draft has no id yet → a patient-wide
        // 'prescription' grant is what authorizes a consent-denied open.
        var patient = await patients.GetByIdAsync(booking.PatientId, ct);
        BreakGlassGrant? grant = null;
        if (patient is null || !patient.HasActiveConsent)
        {
            grant = await breakGlass.GetActiveGrantAsync(userId, command.TenantId, booking.PatientId, "prescription", null, ct);
            if (grant is null)
                throw new ForbiddenException("Patient has no active consent; consultation read refused (DPDP).");
        }

        // Get-or-create (idempotent per booking via the partial unique index uq_prescriptions_booking_draft).
        var detail = await clinical.GetDraftByBookingAsync(command.BookingId, command.TenantId, ct);
        if (detail is null)
        {
            var encCtx = new EncryptionContext(userId, command.TenantId, "prescription", booking.PatientId, ctx.IpAddress);
            // medications is NOT NULL jsonb and the read path always decrypts it → seed a ciphertext of "[]".
            var emptyMedsEnc = await encryption.EncryptAsync(PrescriptionFields.Medications, command.TenantId, "[]", encCtx, ct);
            var draft = Prescription.Draft(
                command.BookingId, booking.PatientId, booking.DoctorId, command.TenantId, emptyMedsEnc, userId, clock.UtcNow);

            const string savepoint = "consult_draft";
            await uow.CreateSavepointAsync(savepoint, ct);
            try
            {
                await clinical.AddPrescriptionAsync(draft, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A concurrent open won the partial-unique race → the INSERT's unique violation aborts the PG tx;
                // roll back to the savepoint to un-abort it, then adopt the winner. If no draft is now visible the
                // error was NOT the benign race → rethrow.
                await uow.RollbackToSavepointAsync(savepoint, ct);
                if (await clinical.GetDraftByBookingAsync(command.BookingId, command.TenantId, ct) is null) throw;
            }

            detail = await clinical.GetDraftByBookingAsync(command.BookingId, command.TenantId, ct)
                ?? throw new InvalidOperationException("Consultation draft could not be created.");
        }

        // Purpose-of-use at read time (break-glass aware) — the draft returns decrypted PHI.
        await purpose.RecordAsync(new PurposeOfUseEntry(
            userId, command.TenantId, "prescription", detail.Prescription.PrescriptionId, command.DeclaredPurpose,
            null, grant is not null, grant?.Justification), ct);

        return await BuildDraftDtoAsync(detail, patient?.FullName, userId, command.TenantId, ct);
    }

    private async Task<ConsultationDraftDto> BuildDraftDtoAsync(
        PrescriptionDetail detail, string? patientName, Guid userId, Guid tenantId, CancellationToken ct)
    {
        var p = detail.Prescription;
        var encCtx = new EncryptionContext(userId, tenantId, "prescription", p.PatientId, ctx.IpAddress);
        return new ConsultationDraftDto(
            p.PrescriptionId, p.PrescriptionNumber, p.BookingId, p.PatientId, patientName, p.Status,
            ConsultationJson.ParseVitals(p.Vitals),
            await DecOrNull(p.ChiefComplaintsEnc, PrescriptionFields.ChiefComplaints, encCtx, ct),
            await DecOrNull(p.ExaminationEnc, PrescriptionFields.Examination, encCtx, ct),
            await DecOrNull(p.DiagnosisEnc, PrescriptionFields.Diagnosis, encCtx, ct),
            await encryption.DecryptAsync(PrescriptionFields.Medications, p.MedicationsEnc, encCtx, ct),
            ConsultationJson.ParseInvestigations(p.Investigations),
            p.Advice, p.FollowUpInDays,
            new DateTimeOffset(DateTime.SpecifyKind(p.UpdatedAt, DateTimeKind.Utc)));
    }

    private async Task<string?> DecOrNull(string? envelope, FieldRef field, EncryptionContext c, CancellationToken ct)
        => string.IsNullOrEmpty(envelope) ? null : await encryption.DecryptAsync(field, envelope, c, ct);
}

// ---- Save (autosave) draft fields — encrypts clinical fields; returns no PHI (204 at the API) ------

public sealed record SaveConsultationCommand(Guid TenantId, Guid ConsultationId, SaveConsultationRequest Request) : ICommand;

public sealed class SaveConsultationValidator : AbstractValidator<SaveConsultationCommand>
{
    public SaveConsultationValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.ConsultationId).NotEmpty();
    }
}

public sealed class SaveConsultationCommandHandler(
    IClinicalRepository clinical,
    IFieldEncryptionService encryption,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx,
    IClock clock)
    : ICommandHandler<SaveConsultationCommand, Unit>
{
    public async Task<Unit> Handle(SaveConsultationCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var req = command.Request;

        var detail = await clinical.GetPrescriptionAsync(command.ConsultationId, command.TenantId, ct)
            ?? throw new KeyNotFoundException("Consultation not found.");   // RLS also blocks cross-tenant
        if (!string.Equals(detail.Prescription.Status, "draft", StringComparison.Ordinal))
            throw new ConflictException($"Consultation cannot be edited (status: {detail.Prescription.Status}). Only a draft is editable.");

        var encCtx = new EncryptionContext(userId, command.TenantId, "prescription", detail.Prescription.PatientId, ctx.IpAddress);
        var chiefEnc = await EncOrNull(req.ChiefComplaints, PrescriptionFields.ChiefComplaints, command.TenantId, encCtx, ct);
        var examEnc = await EncOrNull(req.Examination, PrescriptionFields.Examination, command.TenantId, encCtx, ct);
        var diagEnc = await EncOrNull(req.Diagnosis, PrescriptionFields.Diagnosis, command.TenantId, encCtx, ct);
        var medsEnc = req.MedicationsJson is null
            ? null
            : await encryption.EncryptAsync(PrescriptionFields.Medications, command.TenantId, req.MedicationsJson, encCtx, ct);
        var vitalsJson = req.Vitals is null ? null : ConsultationJson.SerializeVitals(req.Vitals);
        var invJson = req.Investigations is null ? null : ConsultationJson.SerializeInvestigations(req.Investigations);

        // COALESCE per field in the repo → a null (unsent) field leaves the stored value untouched. False means the
        // row left 'draft' between our read and the write (a concurrent finalize) → 409.
        if (!await clinical.UpdateDraftAsync(
                command.ConsultationId, command.TenantId, chiefEnc, examEnc, diagEnc, medsEnc,
                vitalsJson, invJson, req.Advice, req.FollowUpInDays, clock.UtcNow, ct))
            throw new ConflictException("Consultation is no longer a draft (it was finalized concurrently).");

        await audit.RecordAsync(new AuditEntry(
            "draft_save", "prescription", command.ConsultationId, detail.Prescription.PrescriptionNumber, userId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: "Consultation draft saved (clinical fields encrypted)",
            Purpose: "treatment", LegalBasis: "consent"), ct);

        return Unit.Value;
    }

    private async Task<string?> EncOrNull(string? value, FieldRef field, Guid tenantId, EncryptionContext c, CancellationToken ct)
        => value is null ? null : await encryption.EncryptAsync(field, tenantId, value, c, ct);
}

// ---- Finalize (the doctor's signing act; server-derived author; drug-safety gate) -----------------

/// <summary>The doctor's sign act (draft → finalized). Returns alerts carrying medication names → IDoNotCacheResponse.</summary>
public sealed record FinalizeConsultationCommand(Guid TenantId, Guid ConsultationId, FinalizeConsultationRequest Request)
    : ICommand<FinalizeConsultationResult>, IDoNotCacheResponse;

public sealed class FinalizeConsultationValidator : AbstractValidator<FinalizeConsultationCommand>
{
    public FinalizeConsultationValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.ConsultationId).NotEmpty();
    }
}

public sealed class FinalizeConsultationCommandHandler(
    IClinicalRepository clinical,
    IFieldEncryptionService encryption,
    IDrugSafetyScreeningService screening,
    IBookingEventPublisher events,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx,
    IClock clock)
    : ICommandHandler<FinalizeConsultationCommand, FinalizeConsultationResult>
{
    private static readonly HashSet<string> Blocking = new(StringComparer.Ordinal) { "high", "critical" };

    public async Task<FinalizeConsultationResult> Handle(FinalizeConsultationCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");

        var detail = await clinical.GetPrescriptionAsync(command.ConsultationId, command.TenantId, ct)
            ?? throw new KeyNotFoundException("Consultation not found.");   // RLS also blocks cross-tenant
        var p = detail.Prescription;
        if (!string.Equals(p.Status, "draft", StringComparison.Ordinal))
            throw new ConflictException($"Consultation cannot be finalized (status: {p.Status}). Only a draft can be signed.");

        // Server-derived author (MCI: only a registered doctor prescribes). NEVER trust the draft's provisional
        // doctor_id — resolve the caller's own doctor row.
        var doctorId = await clinical.GetDoctorByUserIdAsync(userId, command.TenantId, ct)
            ?? throw new ForbiddenException("Only a registered doctor can finalize a prescription.");

        // Drug-safety screening — idempotent across retries: only screen when no alerts exist yet for this
        // prescription (a prior finalize attempt that returned finalized:false already generated them). The
        // screening service never DELETEs, and this guard prevents duplicate rows on the override retry.
        var existing = await clinical.ListDrugAlertsAsync(command.ConsultationId, command.TenantId, ct);
        if (existing.Count == 0)
        {
            var encCtx = new EncryptionContext(userId, command.TenantId, "prescription", p.PatientId, ctx.IpAddress);
            var medsPlain = await encryption.DecryptAsync(PrescriptionFields.Medications, p.MedicationsEnc, encCtx, ct);
            await screening.ScreenPrescriptionAsync(command.ConsultationId, p.PatientId, command.TenantId, medsPlain, ct);
        }

        // Block signing on unoverridden high/critical alerts unless an override reason is supplied.
        var blocking = (await clinical.ListUnoverriddenAlertsAsync(command.ConsultationId, command.TenantId, ct))
            .Where(a => Blocking.Contains(a.Severity)).ToList();
        var overrideReason = command.Request.OverrideReason?.Trim();
        if (blocking.Count > 0 && string.IsNullOrWhiteSpace(overrideReason))
            return new FinalizeConsultationResult(false, p.PrescriptionId, p.PrescriptionNumber, blocking.Select(ToDto).ToList());

        if (!string.IsNullOrWhiteSpace(overrideReason))
            await clinical.MarkAlertsOverriddenAsync(command.ConsultationId, command.TenantId, userId, overrideReason, clock.UtcNow, ct);

        if (!await clinical.FinalizeAsync(command.ConsultationId, command.TenantId, doctorId, userId, clock.UtcNow, ct))
            throw new ConflictException("Consultation is no longer a draft (it was finalized concurrently).");

        // Audit records drafted-by vs finalized-by distinctly (author/signer separation).
        await audit.RecordAsync(new AuditEntry(
            "finalize", "prescription", p.PrescriptionId, p.PrescriptionNumber, userId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Consultation finalized (drafted_by {p.DraftedByUserId?.ToString() ?? "n/a"}; finalized_by {userId})"
                           + (string.IsNullOrWhiteSpace(overrideReason) ? "" : "; drug-safety alerts overridden"),
            Purpose: "treatment", LegalBasis: "consent"), ct);

        // Integration event — IDs ONLY, NO PHI (same shape as IssuePrescription).
        await events.PublishAsync("docslot.prescription.issued", command.TenantId, p.PrescriptionId, p.PrescriptionNumber,
            new { prescription_id = p.PrescriptionId, patient_id = p.PatientId, booking_id = p.BookingId }, ct);

        return new FinalizeConsultationResult(true, p.PrescriptionId, p.PrescriptionNumber, []);
    }

    private static DrugAlertDto ToDto(DrugAlert a) => new(
        a.AlertId, a.AlertType, a.Severity, a.MedicationName, a.Description, a.Overridden,
        new DateTimeOffset(DateTime.SpecifyKind(a.CreatedAt, DateTimeKind.Utc)));
}
