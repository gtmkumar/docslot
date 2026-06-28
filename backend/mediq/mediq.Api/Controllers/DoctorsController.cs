using mediq.Api.Authorization;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Docslot.Doctors;
using mediq.Application.Features.Docslot.Queries;
using mediq.SharedDataModel.Docslot.Doctors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>Doctor directory + slot availability. Gated by <c>docslot.doctor.read</c> / <c>docslot.slot.read</c>.</summary>
[ApiController]
[Route("api/v1/doctors")]
[Authorize]
public sealed class DoctorsController(
    ICommandDispatcher commands, IQueryDispatcher queries, ICurrentUserContext currentUser) : ControllerBase
{
    [HttpGet]
    [RequirePermission("docslot.doctor.read")]
    [ProducesResponseType<IReadOnlyList<DoctorDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DoctorDto>>> List(CancellationToken ct)
        => Ok(await queries.Query(new ListDoctorsQuery(RequireTenant()), ct));

    /// <summary>
    /// Provision a doctor into the caller's tenant. Gated by <c>docslot.doctor.create</c> (tenant_owner holds
    /// it). tenant_id comes from the JWT (never a header); an Idempotency-Key header is honoured if present.
    /// </summary>
    [HttpPost]
    [RequirePermission("docslot.doctor.create")]
    [ProducesResponseType<CreateDoctorResult>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CreateDoctorResult>> Create([FromBody] CreateDoctorRequest request, CancellationToken ct)
    {
        var result = await commands.Send(new CreateDoctorCommand(RequireTenant(), request), ct);
        return CreatedAtAction(nameof(List), new { doctorId = result.DoctorId }, result);
    }

    [HttpGet("{doctorId:guid}/slots")]
    [RequirePermission("docslot.slot.read")]
    [ProducesResponseType<IReadOnlyList<SlotDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SlotDto>>> Slots(
        Guid doctorId, [FromQuery] DateOnly date, CancellationToken ct)
        => Ok(await queries.Query(new GetDoctorSlotsQuery(RequireTenant(), doctorId, date), ct));

    /// <summary>
    /// Materialize bookable slots for a doctor from their weekly schedule over [from,to]. Gated by
    /// <c>docslot.schedule.update</c>; the doctor must belong to the caller's tenant. Idempotent.
    /// </summary>
    [HttpPost("{doctorId:guid}/slots/generate")]
    [RequirePermission("docslot.schedule.update")]
    [ProducesResponseType<GenerateSlotsResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<GenerateSlotsResult>> GenerateSlots(
        Guid doctorId, [FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
        => Ok(await commands.Send(new GenerateSlotsCommand(RequireTenant(), doctorId, from, to), ct));

    // ====================================================================================================
    // Schedule management (weekly schedule blocks + date-specific overrides) and doctor profile lifecycle.
    // EVERY endpoint cross-tenant-guards the route doctorId in the handler (ExistsInTenantAsync) before any
    // read/mutate — the route id is never trusted alone. tenant_id is always RequireTenant() (JWT), never a
    // header/body. The two writes that change availability re-materialize the rolling horizon in the handler.
    // ====================================================================================================

    /// <summary>List the doctor's recurring weekly schedule blocks. Gated by <c>docslot.doctor.read</c>.</summary>
    [HttpGet("{doctorId:guid}/schedules")]
    [RequirePermission("docslot.doctor.read")]
    [ProducesResponseType<IReadOnlyList<ScheduleBlockDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ScheduleBlockDto>>> GetSchedules(Guid doctorId, CancellationToken ct)
        => Ok(await queries.Query(new GetDoctorSchedulesQuery(RequireTenant(), doctorId), ct));

    /// <summary>
    /// REPLACE the doctor's entire weekly schedule with the supplied blocks (delete-then-insert in one
    /// transaction), then re-materialize the rolling horizon so new availability shows immediately. Each block
    /// is validated server-side (end>start, valid break pair, dayOfWeek 0..6, duration 5..480) with the DB CHECK
    /// constraints as backstop. Gated by <c>docslot.schedule.update</c>.
    /// </summary>
    [HttpPut("{doctorId:guid}/schedules")]
    [RequirePermission("docslot.schedule.update")]
    [ProducesResponseType<ReplaceScheduleResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ReplaceScheduleResult>> ReplaceSchedules(
        Guid doctorId, [FromBody] ReplaceScheduleRequest request, CancellationToken ct)
        => Ok(await commands.Send(new ReplaceDoctorScheduleCommand(RequireTenant(), doctorId, request), ct));

    /// <summary>
    /// List the doctor's date-specific schedule overrides, optionally from a <c>?from</c> date (inclusive).
    /// Gated by <c>docslot.doctor.read</c>.
    /// </summary>
    [HttpGet("{doctorId:guid}/schedule-overrides")]
    [RequirePermission("docslot.doctor.read")]
    [ProducesResponseType<IReadOnlyList<ScheduleOverrideDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ScheduleOverrideDto>>> GetOverrides(
        Guid doctorId, [FromQuery] DateOnly? from, CancellationToken ct)
        => Ok(await queries.Query(new GetScheduleOverridesQuery(RequireTenant(), doctorId, from), ct));

    /// <summary>
    /// Add/replace a date-specific override (UPSERT on (doctor_id, override_date)), then re-materialize the
    /// horizon. Gated by <c>docslot.schedule.update</c>.
    /// </summary>
    [HttpPost("{doctorId:guid}/schedule-overrides")]
    [RequirePermission("docslot.schedule.update")]
    [ProducesResponseType<UpsertOverrideResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<UpsertOverrideResult>> UpsertOverride(
        Guid doctorId, [FromBody] UpsertScheduleOverrideRequest request, CancellationToken ct)
        => Ok(await commands.Send(new UpsertScheduleOverrideCommand(RequireTenant(), doctorId, request), ct));

    /// <summary>Remove a date-specific override, then re-materialize the horizon. Gated by <c>docslot.schedule.update</c>.</summary>
    [HttpDelete("{doctorId:guid}/schedule-overrides/{overrideId:guid}")]
    [RequirePermission("docslot.schedule.update")]
    [ProducesResponseType<DeleteOverrideResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeleteOverrideResult>> DeleteOverride(
        Guid doctorId, Guid overrideId, CancellationToken ct)
        => Ok(await commands.Send(new DeleteScheduleOverrideCommand(RequireTenant(), doctorId, overrideId), ct));

    /// <summary>
    /// Update the WHITELISTED mutable columns of a doctor profile (full_name, display_name, specialization,
    /// department_id, consultation_fee, phone, email, is_active, is_accepting_new_patients). Immutable columns
    /// (tenant_id, user_id, nmc_*) can never be changed here. Gated by <c>docslot.doctor.update</c>.
    /// </summary>
    [HttpPut("{doctorId:guid}")]
    [RequirePermission("docslot.doctor.update")]
    [ProducesResponseType<UpdateDoctorResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<UpdateDoctorResult>> Update(
        Guid doctorId, [FromBody] UpdateDoctorRequest request, CancellationToken ct)
        => Ok(await commands.Send(new UpdateDoctorCommand(RequireTenant(), doctorId, request), ct));

    /// <summary>SOFT-delete a doctor (sets deleted_at; never a hard delete). Gated by <c>docslot.doctor.delete</c>.</summary>
    [HttpDelete("{doctorId:guid}")]
    [RequirePermission("docslot.doctor.delete")]
    [ProducesResponseType<DeleteDoctorResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeleteDoctorResult>> Delete(Guid doctorId, CancellationToken ct)
        => Ok(await commands.Send(new DeleteDoctorCommand(RequireTenant(), doctorId), ct));

    private Guid RequireTenant() =>
        currentUser.TenantId ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");
}
