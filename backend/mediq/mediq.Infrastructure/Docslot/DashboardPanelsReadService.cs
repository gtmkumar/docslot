using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.Dashboard;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Docslot;

/// <summary>
/// Read-side projections for the dashboard's three side panels (WhatsApp agent, department load,
/// on-the-floor doctors). Raw-SQL aggregates mirroring <see cref="BookingReadService"/>: tenant-scoped,
/// "today" computed in Asia/Kolkata, no PHI (doctor identity + aggregate counts only). Missing data
/// projects as zeros/empty — never throws.
/// </summary>
public sealed class DashboardPanelsReadService(PlatformDbContext db) : IDashboardPanelsReadService
{
    /// <summary>Token color keys assigned to department rows by display order (never hex — REACT_SKILL).</summary>
    private static readonly string[] ColorKeyRotation = ["accent", "info", "primary", "warn", "muted"];

    public async Task<IReadOnlyList<DepartmentLoadDto>> GetDepartmentLoadAsync(Guid tenantId, CancellationToken ct)
    {
        // Live capacity vs booked per department, over today's IST slots. Departments with no slots
        // today are omitted (nothing "live" to show). current_count/max_count are the slot occupancy
        // counters the booking pipeline maintains.
        var rows = await db.Database.SqlQueryRaw<DeptLoadRow>(
                """
                SELECT dep.department_id AS "DepartmentId", dep.name AS "Name",
                       COALESCE(SUM(t.current_count), 0)::int AS "Booked",
                       COALESCE(SUM(t.max_count), 0)::int AS "Capacity"
                FROM docslot.departments dep
                JOIN docslot.doctors d ON d.department_id = dep.department_id AND d.tenant_id = dep.tenant_id
                     AND d.deleted_at IS NULL AND d.is_active = true
                JOIN docslot.time_slots t ON t.doctor_id = d.doctor_id
                     AND t.slot_date = (NOW() AT TIME ZONE 'Asia/Kolkata')::date
                WHERE dep.tenant_id = @p0 AND dep.is_active = true
                GROUP BY dep.department_id, dep.name, dep.display_order
                ORDER BY dep.display_order, dep.name
                """,
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);

        return rows.Select((r, i) => new DepartmentLoadDto(
            r.DepartmentId, r.Name, ColorKeyRotation[i % ColorKeyRotation.Length], r.Booked, r.Capacity)).ToList();
    }

    public async Task<IReadOnlyList<FloorDoctorDto>> GetFloorDoctorsAsync(Guid tenantId, CancellationToken ct)
    {
        // "On the floor" = active doctors with OPD slots today (IST). NextSlot = the next still-available
        // slot today (null once they're fully booked / done for the day); SeenToday = completed bookings
        // on today's slots. The inner JOIN LATERAL on slot existence is the "has OPD today" filter.
        var rows = await db.Database.SqlQueryRaw<FloorRow>(
                """
                SELECT d.doctor_id AS "DoctorId",
                       COALESCE(d.display_name, d.full_name) AS "Name",
                       d.specialization AS "Specialization",
                       dep.name AS "DepartmentName",
                       nxt.slot_date AS "NextSlotDate", nxt.start_time AS "NextSlotStart",
                       COALESCE(seen.seen_count, 0)::int AS "SeenToday"
                FROM docslot.doctors d
                LEFT JOIN docslot.departments dep ON dep.department_id = d.department_id
                JOIN LATERAL (
                    SELECT 1 FROM docslot.time_slots t
                    WHERE t.doctor_id = d.doctor_id
                      AND t.slot_date = (NOW() AT TIME ZONE 'Asia/Kolkata')::date
                    LIMIT 1
                ) has_opd ON true
                LEFT JOIN LATERAL (
                    SELECT t.slot_date, t.start_time
                    FROM docslot.time_slots t
                    WHERE t.doctor_id = d.doctor_id
                      AND t.slot_date = (NOW() AT TIME ZONE 'Asia/Kolkata')::date
                      AND t.status = 'available'
                      AND t.current_count < t.max_count
                      AND (t.slot_date + t.start_time) > (NOW() AT TIME ZONE 'Asia/Kolkata')
                    ORDER BY t.start_time ASC
                    LIMIT 1
                ) nxt ON true
                LEFT JOIN LATERAL (
                    SELECT COUNT(*)::int AS seen_count
                    FROM docslot.bookings b
                    JOIN docslot.time_slots s ON s.slot_id = b.slot_id
                    WHERE b.doctor_id = d.doctor_id AND b.tenant_id = d.tenant_id
                      AND b.status = 'completed'
                      AND s.slot_date = (NOW() AT TIME ZONE 'Asia/Kolkata')::date
                ) seen ON true
                WHERE d.tenant_id = @p0 AND d.deleted_at IS NULL AND d.is_active = true
                ORDER BY COALESCE(d.display_name, d.full_name)
                LIMIT 8
                """,
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);

        return rows.Select(r => new FloorDoctorDto(
            r.DoctorId, r.Name, r.Specialization, r.DepartmentName,
            r.NextSlotDate is null || r.NextSlotStart is null
                ? null
                : new DateTimeOffset(r.NextSlotDate.Value.ToDateTime(r.NextSlotStart.Value), DashboardContract.TimeZoneOffset),
            r.SeenToday)).ToList();
    }

    public async Task<AgentPanelDto> GetAgentPanelAsync(Guid tenantId, CancellationToken ct)
    {
        // ---- conversation activity (wa_message_log, rolling 24h) ----------------------------------
        var activityRows = await db.Database.SqlQueryRaw<ActivityRow>(
                """
                SELECT COUNT(DISTINCT w.patient_id)::int AS "ActivePatients",
                       COALESCE((
                           SELECT AVG(EXTRACT(EPOCH FROM (o.first_out - i.sent_at)) / 60.0)
                           FROM docslot.wa_message_log i
                           JOIN LATERAL (
                               SELECT MIN(o2.sent_at) AS first_out
                               FROM docslot.wa_message_log o2
                               WHERE o2.tenant_id = i.tenant_id AND o2.patient_id = i.patient_id
                                 AND o2.direction = 'outbound' AND o2.sent_at > i.sent_at
                           ) o ON o.first_out IS NOT NULL
                           WHERE i.tenant_id = @p0 AND i.direction = 'inbound'
                             AND i.sent_at >= NOW() - INTERVAL '24 hours'
                       ), 0)::numeric AS "AvgResponseMins"
                FROM docslot.wa_message_log w
                WHERE w.tenant_id = @p0 AND w.sent_at >= NOW() - INTERVAL '24 hours'
                """,
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);
        var activity = activityRows.FirstOrDefault() ?? new ActivityRow(0, 0m);

        // Hourly message-volume buckets for the sparkline (23 = oldest hour, 0 = the current hour).
        var bucketRows = await db.Database.SqlQueryRaw<BucketRow>(
                """
                SELECT FLOOR(EXTRACT(EPOCH FROM (NOW() - w.sent_at)) / 3600)::int AS "HoursAgo",
                       COUNT(*)::int AS "Count"
                FROM docslot.wa_message_log w
                WHERE w.tenant_id = @p0 AND w.sent_at >= NOW() - INTERVAL '24 hours'
                GROUP BY 1
                """,
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);
        var byHoursAgo = bucketRows.Where(b => b.HoursAgo is >= 0 and < 24).ToDictionary(b => b.HoursAgo, b => b.Count);
        var peak = byHoursAgo.Count > 0 ? byHoursAgo.Values.Max() : 0;
        var sparkline = new decimal[24];
        for (var hoursAgo = 23; hoursAgo >= 0; hoursAgo--)
        {
            byHoursAgo.TryGetValue(hoursAgo, out var count);
            sparkline[23 - hoursAgo] = peak > 0 ? Math.Round((decimal)count / peak, 2) : 0m;
        }

        // ---- today's WhatsApp booking funnel (IST) + handed-to-desk overlap ------------------------
        // Stage counts are distinct patients over today's whatsapp bookings; "Handed" = greeted patients
        // who ALSO have a non-whatsapp booking today (the desk took over). See AgentPanelDto docs for
        // the proxy definitions.
        var funnelRows = await db.Database.SqlQueryRaw<FunnelRow>(
                """
                SELECT
                    COUNT(DISTINCT b.patient_id)::int AS "Greeted",
                    COUNT(DISTINCT b.patient_id) FILTER (WHERE b.department_id IS NOT NULL)::int AS "SelectedDept",
                    COUNT(DISTINCT b.patient_id) FILTER (WHERE b.slot_id IS NOT NULL)::int AS "PickedSlot",
                    COUNT(DISTINCT b.patient_id) FILTER (WHERE b.status IN ('confirmed','completed'))::int AS "Confirmed",
                    COUNT(DISTINCT b.patient_id) FILTER (WHERE EXISTS (
                        SELECT 1 FROM docslot.bookings h
                        WHERE h.tenant_id = b.tenant_id AND h.patient_id = b.patient_id
                          AND h.booked_via <> 'whatsapp'
                          AND (h.booked_at AT TIME ZONE 'Asia/Kolkata')::date = (NOW() AT TIME ZONE 'Asia/Kolkata')::date
                    ))::int AS "Handed"
                FROM docslot.bookings b
                WHERE b.tenant_id = @p0
                  AND b.booked_via = 'whatsapp'
                  AND (b.booked_at AT TIME ZONE 'Asia/Kolkata')::date = (NOW() AT TIME ZONE 'Asia/Kolkata')::date
                """,
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);
        var f = funnelRows.FirstOrDefault() ?? new FunnelRow(0, 0, 0, 0, 0);

        // Clamp the funnel monotonic non-increasing (mirrors AnalyticsReadService — the FILTERs are
        // subsets, but distinct-patient populations could in theory re-order).
        var greeted = f.Greeted;
        var dept = Math.Min(f.SelectedDept, greeted);
        var slot = Math.Min(f.PickedSlot, dept);
        var confirmed = Math.Min(f.Confirmed, slot);
        var handed = Math.Min(f.Handed, greeted);
        var droppedOff = Math.Max(0, greeted - confirmed - handed);

        return new AgentPanelDto(
            activity.ActivePatients,
            sparkline,
            Math.Round(activity.AvgResponseMins, 1),
            Pct(confirmed, greeted),
            Pct(handed, greeted),
            Pct(droppedOff, greeted),
            [
                new AgentFunnelStageDto("greeted", greeted, Pct(greeted, greeted)),
                new AgentFunnelStageDto("selectedDept", dept, Pct(dept, greeted)),
                new AgentFunnelStageDto("pickedSlot", slot, Pct(slot, greeted)),
                new AgentFunnelStageDto("confirmed", confirmed, Pct(confirmed, greeted)),
            ]);
    }

    private static decimal Pct(int numerator, int denominator) =>
        denominator > 0 ? Math.Round((decimal)numerator * 100m / denominator, 1) : 0m;

    private sealed record DeptLoadRow(Guid DepartmentId, string Name, int Booked, int Capacity);
    private sealed record FloorRow(
        Guid DoctorId, string Name, string? Specialization, string? DepartmentName,
        DateOnly? NextSlotDate, TimeOnly? NextSlotStart, int SeenToday);
    private sealed record ActivityRow(int ActivePatients, decimal AvgResponseMins);
    private sealed record BucketRow(int HoursAgo, int Count);
    private sealed record FunnelRow(int Greeted, int SelectedDept, int PickedSlot, int Confirmed, int Handed);
}
