using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Docslot.Patients;

/// <summary>
/// Reads a single patient's booking-core detail. DPDP: every full patient-record read MUST declare a
/// purpose-of-use (logged to <c>platform.purpose_of_use_log</c>) AND the patient must have an active
/// consent on file — otherwise the read is refused (403). NO clinical PHI (prescriptions/labs/ABDM) is
/// served here; those are deferred to slice 03b/05 behind encryption + RLS + ABDM consent.
/// </summary>
public sealed record GetPatientDetailQuery(Guid TenantId, Guid PatientId, string DeclaredPurpose, string? PurposeNotes)
    : IQuery<PatientDetailDto>;

public sealed class GetPatientDetailValidator : AbstractValidator<GetPatientDetailQuery>
{
    public GetPatientDetailValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.DeclaredPurpose).NotEmpty()
            .WithMessage("A declared purpose-of-use is required to read a patient record (DPDP).");
    }
}

public sealed class GetPatientDetailQueryHandler(
    IPatientReadService reads,
    IPatientRepository patients,
    IPurposeOfUseWriter purposeOfUse,
    ITenantSecurityPolicyService securityPolicy,
    IPermissionContext permissions,
    ICurrentUserContext ctx)
    : IQueryHandler<GetPatientDetailQuery, PatientDetailDto>
{
    public async Task<PatientDetailDto> Handle(GetPatientDetailQuery q, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");

        // The patient must be linked to the caller's tenant (cross-tenant isolation).
        if (!await patients.IsLinkedToTenantAsync(q.PatientId, q.TenantId, ct))
            throw new KeyNotFoundException("Patient not found in this tenant.");

        // Consent gate (DPDP): refuse a full record read without an active consent on file.
        var patient = await patients.GetByIdAsync(q.PatientId, ct)
            ?? throw new KeyNotFoundException("Patient not found.");
        if (!patient.HasActiveConsent)
            throw new ForbiddenException("Patient has no active consent on file; record read refused (DPDP).");

        // Record the declared purpose-of-use BEFORE returning the data.
        await purposeOfUse.RecordAsync(new PurposeOfUseEntry(
            userId, q.TenantId, "patient", q.PatientId, q.DeclaredPurpose, q.PurposeNotes,
            IsBreakGlass: false, BreakGlassReason: null), ct);

        // Issue #91 receptionist masking: mask the phone when the tenant enables it AND the caller is not
        // clinical staff (clinical staff hold medical_history.read and see full contact details). Flipping the
        // tenant toggle therefore actually changes what a front-desk user sees — a REAL enforcement, not cosmetic.
        var policy = await securityPolicy.GetAsync(q.TenantId, ct);
        var maskPhone = policy.MaskSensitiveForReceptionist
                        && !permissions.Has(SecurityPolicyPermissions.ClinicalReadKey);

        return await reads.GetDetailAsync(q.TenantId, q.PatientId, maskPhone, ct)
            ?? throw new KeyNotFoundException("Patient not found.");
    }
}

// ---- Register patient (gated on docslot.patient.update — patient.create not in seed; FLAGGED) -----

public sealed record RegisterPatientCommand(Guid TenantId, RegisterPatientRequest Request)
    : ICommand<RegisterPatientResult>;

public sealed record RegisterPatientRequest(
    string PhoneNumber, string? FullName, short? Age, string? Gender, string PreferredLanguage = "en");

public sealed record RegisterPatientResult(Guid PatientId, bool AlreadyExisted);

public sealed class RegisterPatientValidator : AbstractValidator<RegisterPatientCommand>
{
    public RegisterPatientValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.PhoneNumber).NotEmpty().MaximumLength(15);
    }
}

public sealed class RegisterPatientCommandHandler(
    IPatientRepository patients, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<RegisterPatientCommand, RegisterPatientResult>
{
    public async Task<RegisterPatientResult> Handle(RegisterPatientCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var req = command.Request;

        // Cross-tenant identity by phone: reuse an existing patient if the phone is known.
        var existing = await patients.GetByPhoneAsync(req.PhoneNumber, ct);
        var patientId = existing?.PatientId
            ?? await patients.CreateAsync(req.PhoneNumber, req.FullName, req.Age, req.Gender, req.PreferredLanguage, now, ct);

        if (!await patients.IsLinkedToTenantAsync(patientId, command.TenantId, ct))
            await patients.LinkToTenantAsync(patientId, command.TenantId, now, ct);

        await audit.RecordAsync(new AuditEntry(
            "create", "patient", patientId, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: existing is null ? "Registered new patient + tenant link" : "Linked existing patient to tenant"), ct);

        return new RegisterPatientResult(patientId, existing is not null);
    }
}
