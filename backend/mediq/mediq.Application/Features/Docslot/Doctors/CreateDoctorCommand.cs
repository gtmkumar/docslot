using System.Text.Json;
using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Dashboard.Enums;
using mediq.SharedDataModel.Json;

namespace mediq.Application.Features.Docslot.Doctors;

/// <summary>
/// Provisions a doctor into the caller's tenant (<c>docslot.doctors</c>). Unlike a patient (cross-tenant by
/// phone), a doctor belongs to exactly ONE tenant, so the row carries <c>tenant_id</c> from
/// <see cref="ICurrentUserContext"/> (the JWT claim) and the INSERT runs inside the command's tenant-scoped
/// UnitOfWork transaction — RLS applies, and the tenant can NEVER come from a header. Gated by
/// <c>docslot.doctor.create</c> at the API. Honours an Idempotency-Key header when present (the pipeline
/// caches the first response) but does not require one (creating a doctor is not a money/booking mutation).
/// </summary>
public sealed record CreateDoctorCommand(Guid TenantId, CreateDoctorRequest Request)
    : ICommand<CreateDoctorResult>;

/// <summary>
/// Create-doctor input. Maps ONLY to columns that exist on <c>docslot.doctors</c> (database/03_docslot.sql).
/// <c>Gender</c> uses the shared <see cref="Gender"/> enum (string-serialized to the snake_case DB token).
/// <c>Qualifications</c> is the jsonb column (NOT NULL DEFAULT '[]') — omit it to let the DB default apply.
/// <c>Role</c> is intentionally NOT exposed: the column is NOT NULL DEFAULT 'doctor', so the default applies.
/// </summary>
public sealed record CreateDoctorRequest(
    string FullName,
    string? DisplayName = null,
    Guid? DepartmentId = null,
    string? Specialization = null,
    IReadOnlyList<string>? Qualifications = null,
    decimal? ConsultationFee = null,
    Gender? Gender = null,
    string? Phone = null,
    string? Email = null,
    short? ExperienceYears = null,
    bool IsAcceptingNewPatients = true);

public sealed record CreateDoctorResult(Guid DoctorId, string FullName, Guid? DepartmentId);

public sealed class CreateDoctorValidator : AbstractValidator<CreateDoctorCommand>
{
    public CreateDoctorValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Request.DisplayName).MaximumLength(200);
        RuleFor(x => x.Request.Specialization).MaximumLength(100);
        RuleFor(x => x.Request.Phone).MaximumLength(15);
        RuleFor(x => x.Request.ConsultationFee).GreaterThanOrEqualTo(0)
            .When(x => x.Request.ConsultationFee is not null);
        RuleFor(x => x.Request.ExperienceYears).GreaterThanOrEqualTo((short)0)
            .When(x => x.Request.ExperienceYears is not null);
    }
}

public sealed class CreateDoctorCommandHandler(
    IDoctorRepository doctors, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<CreateDoctorCommand, CreateDoctorResult>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CreateDoctorResult> Handle(CreateDoctorCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var req = command.Request;

        // qualifications is jsonb NOT NULL DEFAULT '[]'. Pass a serialized array only when the caller
        // supplied one; otherwise null → the repository omits the column and the DB default applies.
        var qualifications = req.Qualifications is { Count: > 0 }
            ? JsonSerializer.Serialize(req.Qualifications, JsonOptions)
            : null;

        var doctorId = await doctors.CreateAsync(
            new NewDoctor(
                req.FullName,
                req.DisplayName,
                req.DepartmentId,
                req.Specialization,
                qualifications,
                req.ConsultationFee,
                req.Gender?.ToWireToken(),   // enum → snake_case DB token ('male' | 'prefer_not_say' | ...)
                req.Phone,
                req.Email,
                req.ExperienceYears,
                req.IsAcceptingNewPatients),
            command.TenantId, now, ct);

        await audit.RecordAsync(new AuditEntry(
            "create", "doctor", doctorId, req.FullName, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: "Created doctor"), ct);

        return new CreateDoctorResult(doctorId, req.FullName, req.DepartmentId);
    }
}
