namespace mediq.SharedDataModel.Docslot.Doctors;

// =====================================================================================================
// Doctor schedule-management contracts (docslot.doctor_schedules / docslot.schedule_overrides) +
// doctor profile update. These DTOs are the wire shapes for the GET reads and the PUT/POST/DELETE writes
// on DoctorsController. They map ONLY to columns that exist on the canonical schema (database/03_docslot.sql).
//
// Times are TimeOnly ("HH:mm[:ss]" on the wire), dates DateOnly ("yyyy-MM-dd"), and DayOfWeek is the
// Postgres EXTRACT(DOW) convention: SMALLINT 0..6 with 0 = Sunday. The frontend renders these directly.
// =====================================================================================================

/// <summary>
/// One weekly recurring availability block (a row in <c>docslot.doctor_schedules</c>). Used both as the
/// GET projection and as an element of the PUT replace payload. <c>DayOfWeek</c> is 0..6 (0 = Sunday).
/// A null break pair means "no break"; both must be set together (DB CHECK chk_break_time).
/// </summary>
public sealed record ScheduleBlockDto(
    short DayOfWeek,
    TimeOnly StartTime,
    TimeOnly EndTime,
    short SlotDurationMinutes,
    short MaxPatientsPerSlot,
    TimeOnly? BreakStartTime,
    TimeOnly? BreakEndTime,
    bool IsActive);

/// <summary>
/// PUT /api/v1/doctors/{id}/schedules body — the COMPLETE weekly schedule that REPLACES whatever exists
/// (delete-then-insert in one transaction). An empty <c>Blocks</c> list is valid (clears the schedule).
/// </summary>
public sealed record ReplaceScheduleRequest(IReadOnlyList<ScheduleBlockDto> Blocks);

/// <summary>
/// One date-specific schedule override (a row in <c>docslot.schedule_overrides</c>). <c>IsBlocked</c> true
/// means the doctor is unavailable that day (holiday/leave); false with custom times means special hours.
/// </summary>
public sealed record ScheduleOverrideDto(
    Guid OverrideId,
    DateOnly OverrideDate,
    bool IsBlocked,
    TimeOnly? CustomStartTime,
    TimeOnly? CustomEndTime,
    string? Reason);

/// <summary>
/// POST /api/v1/doctors/{id}/schedule-overrides body — UPSERT on (doctor_id, override_date). When
/// <c>IsBlocked</c> is false the custom times define the special hours; when true they are typically null.
/// </summary>
public sealed record UpsertScheduleOverrideRequest(
    DateOnly OverrideDate,
    bool IsBlocked = true,
    TimeOnly? CustomStartTime = null,
    TimeOnly? CustomEndTime = null,
    string? Reason = null);

/// <summary>
/// PUT /api/v1/doctors/{id} body — doctor profile update. Maps ONLY to the WHITELISTED mutable columns on
/// <c>docslot.doctors</c>. Immutable identity/regulatory columns (tenant_id, user_id, nmc_*) are deliberately
/// NOT present and can never be changed through this endpoint. Every field is optional; a null field is left
/// untouched (true PATCH-style semantics on a PUT body — at least one field must be supplied, enforced by the
/// validator).
/// </summary>
public sealed record UpdateDoctorRequest(
    string? FullName = null,
    string? DisplayName = null,
    string? Specialization = null,
    Guid? DepartmentId = null,
    decimal? ConsultationFee = null,
    string? Phone = null,
    string? Email = null,
    bool? IsActive = null,
    bool? IsAcceptingNewPatients = null);
