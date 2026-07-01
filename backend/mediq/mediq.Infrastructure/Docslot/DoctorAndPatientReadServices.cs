using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.Dashboard;
using mediq.SharedDataModel.Docslot.Doctors;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Docslot;

/// <summary>Doctor + slot reads for the booking surface (tenant-scoped, active only).</summary>
public sealed class DoctorReadService(PlatformDbContext db) : IDoctorReadService
{
    public async Task<bool> ExistsInTenantAsync(Guid doctorId, Guid tenantId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<BoolRow>(
            """
            SELECT EXISTS(
                SELECT 1 FROM docslot.doctors
                WHERE doctor_id = @p0 AND tenant_id = @p1 AND deleted_at IS NULL
            ) AS "Value"
            """,
            new NpgsqlParameter("@p0", doctorId),
            new NpgsqlParameter("@p1", tenantId)).ToListAsync(ct);
        return rows.FirstOrDefault()?.Value ?? false;
    }

    private sealed record BoolRow(bool Value);

    public async Task<IReadOnlyList<DoctorDto>> ListAsync(Guid tenantId, CancellationToken ct)
    {
        // Directory cards: base doctor row + additive enrichment (department name, today's slot
        // utilization, average rating, today's schedule hours, next available future slot). Every
        // enrichment is a LEFT JOIN / scalar subquery so missing data projects as NULL — never throws.
        // "today" / weekday are computed in Asia/Kolkata. None of these fields are PHI (aggregate counts
        // + a doctor's own schedule + average rating).
        var rows = await db.Database.SqlQueryRaw<DoctorRow>(
                """
                SELECT
                    d.doctor_id        AS "DoctorId",
                    d.full_name        AS "FullName",
                    d.display_name     AS "DisplayName",
                    d.specialization   AS "Specialization",
                    d.department_id    AS "DepartmentId",
                    d.consultation_fee AS "ConsultationFee",
                    d.is_accepting_new_patients AS "IsAcceptingNewPatients",
                    dep.name           AS "DepartmentName",
                    ts.booked          AS "TodayBooked",
                    ts.capacity        AS "TodayCapacity",
                    rv.avg_rating      AS "Rating",
                    sch.start_time     AS "TodayHoursStart",
                    sch.end_time       AS "TodayHoursEnd",
                    nxt.slot_date      AS "NextSlotDate",
                    nxt.start_time     AS "NextSlotStart"
                FROM docslot.doctors d
                LEFT JOIN docslot.departments dep ON dep.department_id = d.department_id
                LEFT JOIN LATERAL (
                    SELECT SUM(t.current_count)::int AS booked, SUM(t.max_count)::int AS capacity
                    FROM docslot.time_slots t
                    WHERE t.doctor_id = d.doctor_id
                      AND t.slot_date = (NOW() AT TIME ZONE 'Asia/Kolkata')::date
                ) ts ON true
                LEFT JOIN LATERAL (
                    SELECT AVG(r.rating)::numeric(3,2) AS avg_rating
                    FROM docslot.reviews r
                    WHERE r.doctor_id = d.doctor_id AND r.is_published = true
                ) rv ON true
                LEFT JOIN LATERAL (
                    SELECT s.start_time, s.end_time
                    FROM docslot.doctor_schedules s
                    WHERE s.doctor_id = d.doctor_id
                      AND s.is_active = true
                      AND s.day_of_week = EXTRACT(DOW FROM (NOW() AT TIME ZONE 'Asia/Kolkata'))::smallint
                    ORDER BY s.start_time ASC
                    LIMIT 1
                ) sch ON true
                LEFT JOIN LATERAL (
                    SELECT t.slot_date, t.start_time
                    FROM docslot.time_slots t
                    WHERE t.doctor_id = d.doctor_id
                      AND t.status = 'available'
                      AND t.current_count < t.max_count
                      AND (t.slot_date + t.start_time) > (NOW() AT TIME ZONE 'Asia/Kolkata')
                    ORDER BY t.slot_date ASC, t.start_time ASC
                    LIMIT 1
                ) nxt ON true
                WHERE d.tenant_id = @p0 AND d.deleted_at IS NULL AND d.is_active = true
                ORDER BY d.full_name
                """,
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);

        return rows.Select(Map).ToList();
    }

    private static DoctorDto Map(DoctorRow r) => new(
        r.DoctorId, r.FullName, r.DisplayName, r.Specialization,
        r.DepartmentId, r.ConsultationFee, r.IsAcceptingNewPatients,
        r.DepartmentName,
        r.TodayBooked is null ? null : (short)r.TodayBooked.Value,
        r.TodayCapacity is null ? null : (short)r.TodayCapacity.Value,
        r.Rating,
        r.TodayHoursStart,
        r.TodayHoursEnd,
        r.NextSlotDate is null || r.NextSlotStart is null
            ? null
            : new DateTimeOffset(r.NextSlotDate.Value.ToDateTime(r.NextSlotStart.Value), DashboardContract.TimeZoneOffset));

    private sealed record DoctorRow(
        Guid DoctorId, string FullName, string? DisplayName, string? Specialization,
        Guid? DepartmentId, decimal? ConsultationFee, bool IsAcceptingNewPatients,
        string? DepartmentName, int? TodayBooked, int? TodayCapacity, decimal? Rating,
        TimeOnly? TodayHoursStart, TimeOnly? TodayHoursEnd, DateOnly? NextSlotDate, TimeOnly? NextSlotStart);

    public async Task<IReadOnlyList<SlotDto>> GetSlotsAsync(Guid tenantId, Guid doctorId, DateOnly date, CancellationToken ct) =>
        await db.TimeSlots.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.DoctorId == doctorId && s.SlotDate == date)
            .OrderBy(s => s.StartTime)
            .Select(s => new SlotDto(s.SlotId, s.DoctorId, s.SlotDate, s.StartTime, s.EndTime,
                s.Status, s.CurrentCount, s.MaxCount))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ScheduleBlockDto>> GetSchedulesAsync(Guid doctorId, CancellationToken ct)
    {
        // Weekly recurring blocks for the doctor. TimeOnly / SMALLINT map natively in Npgsql. The cross-tenant
        // guard runs in the handler (ExistsInTenantAsync) before this is reached; the projection joins on
        // doctor_id only. NOT PHI — a doctor's own availability windows.
        var rows = await db.Database.SqlQueryRaw<ScheduleRow>(
                """
                SELECT
                    s.day_of_week            AS "DayOfWeek",
                    s.start_time             AS "StartTime",
                    s.end_time               AS "EndTime",
                    s.slot_duration_minutes  AS "SlotDurationMinutes",
                    s.max_patients_per_slot  AS "MaxPatientsPerSlot",
                    s.break_start_time       AS "BreakStartTime",
                    s.break_end_time         AS "BreakEndTime",
                    s.is_active              AS "IsActive"
                FROM docslot.doctor_schedules s
                WHERE s.doctor_id = @p0 AND s.is_active = true
                ORDER BY s.day_of_week, s.start_time
                """,
                new NpgsqlParameter("@p0", doctorId))
            .ToListAsync(ct);

        return rows.Select(r => new ScheduleBlockDto(
            r.DayOfWeek, r.StartTime, r.EndTime, r.SlotDurationMinutes, r.MaxPatientsPerSlot,
            r.BreakStartTime, r.BreakEndTime, r.IsActive)).ToList();
    }

    private sealed record ScheduleRow(
        short DayOfWeek, TimeOnly StartTime, TimeOnly EndTime, short SlotDurationMinutes,
        short MaxPatientsPerSlot, TimeOnly? BreakStartTime, TimeOnly? BreakEndTime, bool IsActive);

    public async Task<IReadOnlyList<ScheduleOverrideDto>> GetOverridesAsync(Guid doctorId, DateOnly? from, CancellationToken ct)
    {
        // Date-specific overrides, optionally bounded below by `from` (inclusive). The untyped-NULL `from`
        // filter is cast to ::date so PostgreSQL can plan the IS NULL OR >= comparison (the untyped-NULL
        // gotcha noted in slice 08b — a bare @p1 with a NULL value otherwise fails to infer a type).
        var rows = await db.Database.SqlQueryRaw<OverrideRow>(
                """
                SELECT
                    o.override_id        AS "OverrideId",
                    o.override_date      AS "OverrideDate",
                    o.is_blocked         AS "IsBlocked",
                    o.custom_start_time  AS "CustomStartTime",
                    o.custom_end_time    AS "CustomEndTime",
                    o.reason             AS "Reason"
                FROM docslot.schedule_overrides o
                WHERE o.doctor_id = @p0
                  AND (@p1::date IS NULL OR o.override_date >= @p1::date)
                  -- Exclude neutralized "deleted" tombstones (see DeleteOverrideAsync): the app role lacks
                  -- DELETE on this table, so a delete neutralizes the row to a no-op marked reason='__deleted__'.
                  AND NOT (o.is_blocked = false AND o.custom_start_time IS NULL AND o.reason = '__deleted__')
                ORDER BY o.override_date
                """,
                new NpgsqlParameter("@p0", doctorId),
                new NpgsqlParameter("@p1", (object?)from ?? DBNull.Value))
            .ToListAsync(ct);

        return rows.Select(r => new ScheduleOverrideDto(
            r.OverrideId, r.OverrideDate, r.IsBlocked, r.CustomStartTime, r.CustomEndTime, r.Reason)).ToList();
    }

    private sealed record OverrideRow(
        Guid OverrideId, DateOnly OverrideDate, bool IsBlocked,
        TimeOnly? CustomStartTime, TimeOnly? CustomEndTime, string? Reason);
}

/// <summary>
/// Patient reads. The LIST is MASKED (PHI — raw phone never serialized) and tenant-linked. The DETAIL is
/// served by the purpose-of-use-gated handler (this service only projects). NO clinical PHI here.
/// </summary>
public sealed class PatientReadService(PlatformDbContext db) : IPatientReadService
{
    public async Task<IReadOnlyList<PatientListItemDto>> ListAsync(Guid tenantId, int skip, int take, CancellationToken ct)
    {
        // Tenant-scoped via the link table (patients are cross-tenant by phone; access is link-mediated).
        var rows = await (
            from l in db.PatientTenantLinks.AsNoTracking()
            join p in db.Patients.AsNoTracking() on l.PatientId equals p.PatientId
            where l.TenantId == tenantId && p.DeletedAt == null
            orderby p.FullName
            select new { p.PatientId, p.FullName, p.PhoneNumber, p.Age, p.Gender, p.PreferredLanguage })
            .Skip(skip).Take(take)
            .ToListAsync(ct);

        return rows.Select(p => new PatientListItemDto(
            p.PatientId, p.FullName, PhoneMasker.Mask(p.PhoneNumber),
            p.Age, p.Gender, p.PreferredLanguage)).ToList();
    }

    public async Task<PatientDetailDto?> GetDetailAsync(Guid tenantId, Guid patientId, bool maskPhone, CancellationToken ct)
    {
        var p = await db.Patients.AsNoTracking()
            .FirstOrDefaultAsync(x => x.PatientId == patientId && x.DeletedAt == null, ct);
        if (p is null) return null;

        // Issue #91: the phone is partially masked for front-desk staff when the tenant enables it, and shown
        // in full to exempt (clinical) staff or when the tenant disables masking. Decision made by the handler.
        var phone = maskPhone ? PhoneMasker.Mask(p.PhoneNumber) : (p.PhoneNumber ?? "");

        return new PatientDetailDto(
            p.PatientId, p.FullName, phone, p.DateOfBirth, p.Age,
            p.Gender, p.Email, p.PreferredLanguage, p.HasActiveConsent);
    }
}
