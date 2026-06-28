using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Doctors;

namespace mediq.Application.Features.Docslot.Doctors;

// =====================================================================================================
// Doctor schedule-management (docslot.doctor_schedules / docslot.schedule_overrides) + doctor profile
// update / soft-delete. Phase-0 vertical slice.
//
// Cross-tenant guard: EVERY command (and the queries' handlers) first verifies the doctor belongs to the
// caller's tenant via IDoctorReadService.ExistsInTenantAsync — the route doctorId is NEVER trusted alone.
// A failed guard throws ForbiddenException (→403). tenant_id always comes from the JWT (the command carries
// it from ICurrentUserContext at the controller), never a header/body.
//
// After any schedule/override change the rolling horizon is re-materialized immediately via
// ISlotGenerationService.GenerateAsync(doctorId, today, today+HorizonDays) so new availability shows at once.
// =====================================================================================================

/// <summary>Days of the rolling horizon re-materialized after a schedule/override change (today .. today+N).</summary>
internal static class ScheduleHorizon
{
    public const int Days = 14;
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
}

// ---- GET /api/v1/doctors/{id}/schedules ----------------------------------------------------------

public sealed record GetDoctorSchedulesQuery(Guid TenantId, Guid DoctorId)
    : IQuery<IReadOnlyList<ScheduleBlockDto>>;

public sealed class GetDoctorSchedulesQueryHandler(IDoctorReadService doctors)
    : IQueryHandler<GetDoctorSchedulesQuery, IReadOnlyList<ScheduleBlockDto>>
{
    public async Task<IReadOnlyList<ScheduleBlockDto>> Handle(GetDoctorSchedulesQuery q, CancellationToken ct)
    {
        if (!await doctors.ExistsInTenantAsync(q.DoctorId, q.TenantId, ct))
            throw new mediq.Utilities.Exceptions.ForbiddenException("Doctor not found in this tenant.");
        return await doctors.GetSchedulesAsync(q.DoctorId, ct);
    }
}

// ---- GET /api/v1/doctors/{id}/schedule-overrides -------------------------------------------------

public sealed record GetScheduleOverridesQuery(Guid TenantId, Guid DoctorId, DateOnly? From)
    : IQuery<IReadOnlyList<ScheduleOverrideDto>>;

public sealed class GetScheduleOverridesQueryHandler(IDoctorReadService doctors)
    : IQueryHandler<GetScheduleOverridesQuery, IReadOnlyList<ScheduleOverrideDto>>
{
    public async Task<IReadOnlyList<ScheduleOverrideDto>> Handle(GetScheduleOverridesQuery q, CancellationToken ct)
    {
        if (!await doctors.ExistsInTenantAsync(q.DoctorId, q.TenantId, ct))
            throw new mediq.Utilities.Exceptions.ForbiddenException("Doctor not found in this tenant.");
        return await doctors.GetOverridesAsync(q.DoctorId, q.From, ct);
    }
}

// ---- PUT /api/v1/doctors/{id}/schedules (replace) ------------------------------------------------

/// <summary>
/// REPLACES the doctor's entire weekly schedule with the supplied blocks (delete-then-insert in one UoW tx),
/// then re-materializes the rolling horizon. Gated by <c>docslot.schedule.update</c>.
/// </summary>
public sealed record ReplaceDoctorScheduleCommand(Guid TenantId, Guid DoctorId, ReplaceScheduleRequest Request)
    : ICommand<ReplaceScheduleResult>;

public sealed record ReplaceScheduleResult(Guid DoctorId, int BlocksSaved, int SlotsCreated);

public sealed class ReplaceDoctorScheduleValidator : AbstractValidator<ReplaceDoctorScheduleCommand>
{
    public ReplaceDoctorScheduleValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.DoctorId).NotEmpty();
        RuleFor(x => x.Request.Blocks).NotNull();
        RuleForEach(x => x.Request.Blocks).SetValidator(new ScheduleBlockValidator());
    }
}

/// <summary>
/// Per-block validation that mirrors the DB CHECK constraints (chk_schedule_time, chk_break_time) plus
/// sensible value ranges. The DB CHECKs remain the backstop (a violation maps to 422 in the repository).
/// </summary>
public sealed class ScheduleBlockValidator : AbstractValidator<ScheduleBlockDto>
{
    public ScheduleBlockValidator()
    {
        RuleFor(b => b.DayOfWeek).InclusiveBetween((short)0, (short)6)
            .WithMessage("dayOfWeek must be 0..6 (0 = Sunday).");
        RuleFor(b => b.EndTime).GreaterThan(b => b.StartTime)
            .WithMessage("endTime must be after startTime.");
        RuleFor(b => b.SlotDurationMinutes).InclusiveBetween((short)5, (short)480)
            .WithMessage("slotDurationMinutes must be between 5 and 480.");
        RuleFor(b => b.MaxPatientsPerSlot).GreaterThanOrEqualTo((short)1)
            .WithMessage("maxPatientsPerSlot must be at least 1.");

        // Break pair must be both-null or both-set with end > start, AND within the working window.
        RuleFor(b => b).Must(b =>
                (b.BreakStartTime is null && b.BreakEndTime is null) ||
                (b.BreakStartTime is not null && b.BreakEndTime is not null && b.BreakEndTime > b.BreakStartTime))
            .WithMessage("breakStartTime/breakEndTime must both be null or both set with breakEnd > breakStart.");
        RuleFor(b => b).Must(b =>
                b.BreakStartTime is null ||
                (b.BreakStartTime >= b.StartTime && b.BreakEndTime <= b.EndTime))
            .WithMessage("the break must fall within the working window (startTime..endTime).");
    }
}

public sealed class ReplaceDoctorScheduleCommandHandler(
    IDoctorReadService doctorReads, IDoctorRepository doctors, ISlotGenerationService slots,
    IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<ReplaceDoctorScheduleCommand, ReplaceScheduleResult>
{
    public async Task<ReplaceScheduleResult> Handle(ReplaceDoctorScheduleCommand command, CancellationToken ct)
    {
        if (!await doctorReads.ExistsInTenantAsync(command.DoctorId, command.TenantId, ct))
            throw new mediq.Utilities.Exceptions.ForbiddenException("Doctor not found in this tenant.");

        var blocks = command.Request.Blocks
            .Select(b => new ScheduleBlock(
                b.DayOfWeek, b.StartTime, b.EndTime, b.SlotDurationMinutes, b.MaxPatientsPerSlot,
                b.BreakStartTime, b.BreakEndTime, b.IsActive))
            .ToList();

        var saved = await doctors.ReplaceSchedulesAsync(command.DoctorId, blocks, ct);

        // Re-materialize the rolling horizon so new availability shows immediately. The generator is idempotent
        // and never clobbers existing/booked slots; it runs on the same tenant-scoped transaction.
        var today = ScheduleHorizon.Today;
        var created = await slots.GenerateAsync(command.DoctorId, today, today.AddDays(ScheduleHorizon.Days), ct);

        await audit.RecordAsync(new AuditEntry(
            "update", "doctor_schedule", command.DoctorId, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Replaced weekly schedule ({saved} blocks); regenerated {created} slots over {ScheduleHorizon.Days}d horizon"), ct);

        return new ReplaceScheduleResult(command.DoctorId, saved, created);
    }
}

// ---- POST /api/v1/doctors/{id}/schedule-overrides (upsert) ---------------------------------------

/// <summary>
/// Adds/replaces a date-specific override (UPSERT on (doctor_id, override_date)), then re-materializes the
/// horizon so a newly blocked/special day reflects immediately. Gated by <c>docslot.schedule.update</c>.
/// </summary>
public sealed record UpsertScheduleOverrideCommand(Guid TenantId, Guid DoctorId, UpsertScheduleOverrideRequest Request)
    : ICommand<UpsertOverrideResult>;

public sealed record UpsertOverrideResult(Guid OverrideId, Guid DoctorId, int SlotsCreated);

public sealed class UpsertScheduleOverrideValidator : AbstractValidator<UpsertScheduleOverrideCommand>
{
    public UpsertScheduleOverrideValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.DoctorId).NotEmpty();
        RuleFor(x => x.Request.OverrideDate).NotEmpty();

        // Custom times are only meaningful for a NON-blocked special-hours override; when supplied, end > start.
        When(x => x.Request.CustomStartTime is not null || x.Request.CustomEndTime is not null, () =>
        {
            RuleFor(x => x.Request.CustomStartTime).NotNull().WithMessage("customStartTime is required when customEndTime is set.");
            RuleFor(x => x.Request.CustomEndTime).NotNull().WithMessage("customEndTime is required when customStartTime is set.");
            RuleFor(x => x.Request).Must(r => r.CustomEndTime is null || r.CustomStartTime is null || r.CustomEndTime > r.CustomStartTime)
                .WithMessage("customEndTime must be after customStartTime.");
        });
        RuleFor(x => x.Request.Reason).MaximumLength(200);
    }
}

public sealed class UpsertScheduleOverrideCommandHandler(
    IDoctorReadService doctorReads, IDoctorRepository doctors, ISlotGenerationService slots,
    IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<UpsertScheduleOverrideCommand, UpsertOverrideResult>
{
    public async Task<UpsertOverrideResult> Handle(UpsertScheduleOverrideCommand command, CancellationToken ct)
    {
        if (!await doctorReads.ExistsInTenantAsync(command.DoctorId, command.TenantId, ct))
            throw new mediq.Utilities.Exceptions.ForbiddenException("Doctor not found in this tenant.");

        var req = command.Request;
        var overrideId = await doctors.UpsertOverrideAsync(
            command.DoctorId,
            new ScheduleOverride(req.OverrideDate, req.IsBlocked, req.CustomStartTime, req.CustomEndTime, req.Reason),
            clock.UtcNow, ct);

        var today = ScheduleHorizon.Today;
        var created = await slots.GenerateAsync(command.DoctorId, today, today.AddDays(ScheduleHorizon.Days), ct);

        await audit.RecordAsync(new AuditEntry(
            "update", "schedule_override", overrideId, req.OverrideDate.ToString("O"), ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Upserted override for {req.OverrideDate:O} (blocked={req.IsBlocked}); regenerated {created} slots"), ct);

        return new UpsertOverrideResult(overrideId, command.DoctorId, created);
    }
}

// ---- DELETE /api/v1/doctors/{id}/schedule-overrides/{overrideId} ---------------------------------

/// <summary>Removes a date-specific override then re-materializes the horizon. Gated by <c>docslot.schedule.update</c>.</summary>
public sealed record DeleteScheduleOverrideCommand(Guid TenantId, Guid DoctorId, Guid OverrideId)
    : ICommand<DeleteOverrideResult>;

public sealed record DeleteOverrideResult(Guid OverrideId, Guid DoctorId, int SlotsCreated);

public sealed class DeleteScheduleOverrideValidator : AbstractValidator<DeleteScheduleOverrideCommand>
{
    public DeleteScheduleOverrideValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.DoctorId).NotEmpty();
        RuleFor(x => x.OverrideId).NotEmpty();
    }
}

public sealed class DeleteScheduleOverrideCommandHandler(
    IDoctorReadService doctorReads, IDoctorRepository doctors, ISlotGenerationService slots,
    IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<DeleteScheduleOverrideCommand, DeleteOverrideResult>
{
    public async Task<DeleteOverrideResult> Handle(DeleteScheduleOverrideCommand command, CancellationToken ct)
    {
        if (!await doctorReads.ExistsInTenantAsync(command.DoctorId, command.TenantId, ct))
            throw new mediq.Utilities.Exceptions.ForbiddenException("Doctor not found in this tenant.");

        var removed = await doctors.DeleteOverrideAsync(command.DoctorId, command.OverrideId, ct);
        if (!removed)
            throw new KeyNotFoundException("Override not found for this doctor.");

        var today = ScheduleHorizon.Today;
        var created = await slots.GenerateAsync(command.DoctorId, today, today.AddDays(ScheduleHorizon.Days), ct);

        await audit.RecordAsync(new AuditEntry(
            "delete", "schedule_override", command.OverrideId, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Removed override {command.OverrideId}; regenerated {created} slots"), ct);

        return new DeleteOverrideResult(command.OverrideId, command.DoctorId, created);
    }
}

// ---- PUT /api/v1/doctors/{id} (update profile) ---------------------------------------------------

/// <summary>
/// Updates the WHITELISTED mutable columns on a doctor profile. Immutable columns (tenant_id, user_id, nmc_*)
/// are not part of the request and can never be changed here. Gated by <c>docslot.doctor.update</c>.
/// </summary>
public sealed record UpdateDoctorCommand(Guid TenantId, Guid DoctorId, UpdateDoctorRequest Request)
    : ICommand<UpdateDoctorResult>;

public sealed record UpdateDoctorResult(Guid DoctorId);

public sealed class UpdateDoctorValidator : AbstractValidator<UpdateDoctorCommand>
{
    public UpdateDoctorValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.DoctorId).NotEmpty();

        // A PUT with an all-null body is a no-op — reject it so the caller gets a clear 422, not a silent 404.
        RuleFor(x => x.Request).Must(HasAnyField)
            .WithMessage("Provide at least one field to update.");

        RuleFor(x => x.Request.FullName).MaximumLength(200).When(x => x.Request.FullName is not null);
        RuleFor(x => x.Request.FullName).NotEmpty().When(x => x.Request.FullName is not null)
            .WithMessage("fullName cannot be set to empty.");
        RuleFor(x => x.Request.DisplayName).MaximumLength(200).When(x => x.Request.DisplayName is not null);
        RuleFor(x => x.Request.Specialization).MaximumLength(100).When(x => x.Request.Specialization is not null);
        RuleFor(x => x.Request.Phone).MaximumLength(15).When(x => x.Request.Phone is not null);
        RuleFor(x => x.Request.Email).EmailAddress().MaximumLength(255).When(x => x.Request.Email is not null);
        RuleFor(x => x.Request.ConsultationFee).GreaterThanOrEqualTo(0).When(x => x.Request.ConsultationFee is not null);
    }

    private static bool HasAnyField(UpdateDoctorRequest r) =>
        r.FullName is not null || r.DisplayName is not null || r.Specialization is not null ||
        r.DepartmentId is not null || r.ConsultationFee is not null || r.Phone is not null ||
        r.Email is not null || r.IsActive is not null || r.IsAcceptingNewPatients is not null;
}

public sealed class UpdateDoctorCommandHandler(
    IDoctorReadService doctorReads, IDoctorRepository doctors, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<UpdateDoctorCommand, UpdateDoctorResult>
{
    public async Task<UpdateDoctorResult> Handle(UpdateDoctorCommand command, CancellationToken ct)
    {
        if (!await doctorReads.ExistsInTenantAsync(command.DoctorId, command.TenantId, ct))
            throw new mediq.Utilities.Exceptions.ForbiddenException("Doctor not found in this tenant.");

        var r = command.Request;
        var updated = await doctors.UpdateAsync(
            command.DoctorId, command.TenantId,
            new DoctorUpdate(r.FullName, r.DisplayName, r.Specialization, r.DepartmentId,
                r.ConsultationFee, r.Phone, r.Email, r.IsActive, r.IsAcceptingNewPatients),
            clock.UtcNow, ct);

        // The ExistsInTenant guard passed, so a no-update here means a concurrent soft-delete; surface as 404.
        if (!updated)
            throw new KeyNotFoundException("Doctor not found in this tenant.");

        await audit.RecordAsync(new AuditEntry(
            "update", "doctor", command.DoctorId, r.FullName, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: "Updated doctor profile"), ct);

        return new UpdateDoctorResult(command.DoctorId);
    }
}

// ---- DELETE /api/v1/doctors/{id} (soft delete) --------------------------------------------------

/// <summary>SOFT-deletes a doctor (sets deleted_at; never hard delete). Gated by <c>docslot.doctor.delete</c>.</summary>
public sealed record DeleteDoctorCommand(Guid TenantId, Guid DoctorId) : ICommand<DeleteDoctorResult>;

public sealed record DeleteDoctorResult(Guid DoctorId);

public sealed class DeleteDoctorValidator : AbstractValidator<DeleteDoctorCommand>
{
    public DeleteDoctorValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.DoctorId).NotEmpty();
    }
}

public sealed class DeleteDoctorCommandHandler(
    IDoctorReadService doctorReads, IDoctorRepository doctors, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<DeleteDoctorCommand, DeleteDoctorResult>
{
    public async Task<DeleteDoctorResult> Handle(DeleteDoctorCommand command, CancellationToken ct)
    {
        if (!await doctorReads.ExistsInTenantAsync(command.DoctorId, command.TenantId, ct))
            throw new mediq.Utilities.Exceptions.ForbiddenException("Doctor not found in this tenant.");

        var removed = await doctors.SoftDeleteAsync(command.DoctorId, command.TenantId, clock.UtcNow, ct);
        if (!removed)
            throw new KeyNotFoundException("Doctor not found in this tenant.");

        await audit.RecordAsync(new AuditEntry(
            "delete", "doctor", command.DoctorId, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: "Soft-deleted doctor"), ct);

        return new DeleteDoctorResult(command.DoctorId);
    }
}
