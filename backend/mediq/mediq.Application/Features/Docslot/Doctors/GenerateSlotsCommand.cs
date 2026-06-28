using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;

namespace mediq.Application.Features.Docslot.Doctors;

/// <summary>
/// Staff-triggered slot materialization for one doctor over a date range (the on-demand counterpart to the
/// nightly materializer). Gated by <c>docslot.schedule.update</c>; the actor's tenant must own the doctor.
/// Idempotent (the DB fn never clobbers existing/booked slots).
/// </summary>
public sealed record GenerateSlotsCommand(Guid TenantId, Guid DoctorId, DateOnly FromDate, DateOnly ToDate)
    : ICommand<GenerateSlotsResult>;

public sealed record GenerateSlotsResult(Guid DoctorId, int SlotsCreated);

public sealed class GenerateSlotsValidator : AbstractValidator<GenerateSlotsCommand>
{
    public GenerateSlotsValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.DoctorId).NotEmpty();
        RuleFor(x => x.ToDate).GreaterThanOrEqualTo(x => x.FromDate).WithMessage("toDate must be >= fromDate.");
        RuleFor(x => x).Must(x => x.ToDate.DayNumber - x.FromDate.DayNumber <= 92)
            .WithMessage("Date range too large (max 92 days).");
    }
}

public sealed class GenerateSlotsCommandHandler(
    IDoctorReadService doctors, ISlotGenerationService slots, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<GenerateSlotsCommand, GenerateSlotsResult>
{
    public async Task<GenerateSlotsResult> Handle(GenerateSlotsCommand command, CancellationToken ct)
    {
        // Cross-tenant guard: the actor may only generate slots for a doctor in their own tenant.
        if (!await doctors.ExistsInTenantAsync(command.DoctorId, command.TenantId, ct))
            throw new mediq.Utilities.Exceptions.ForbiddenException("Doctor not found in this tenant.");

        var created = await slots.GenerateAsync(command.DoctorId, command.FromDate, command.ToDate, ct);

        await audit.RecordAsync(new AuditEntry(
            "generate_slots", "doctor", command.DoctorId, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Generated {created} slots for doctor {command.DoctorId} ({command.FromDate:O}..{command.ToDate:O})"), ct);

        return new GenerateSlotsResult(command.DoctorId, created);
    }
}
