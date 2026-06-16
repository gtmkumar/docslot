using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.Dashboard;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Docslot;

/// <summary>
/// Read-side analytics projections for the Analytics screen. Tenant-scoped; period bounds the booked_at /
/// slot_date range in Asia/Kolkata. Every result is an AGGREGATE (no PHI). Built from raw SQL — one
/// round-trip per sub-result — mirroring <see cref="BookingReadService"/>. Missing data → zeros, never null.
/// </summary>
public sealed class AnalyticsReadService(PlatformDbContext db) : IAnalyticsReadService
{
    public async Task<AnalyticsDto> GetAnalyticsAsync(Guid tenantId, AnalyticsPeriod period, CancellationToken ct)
    {
        // Period start in IST (inclusive); end is "now". 'month' = first of this month, etc.
        var periodSql = period switch
        {
            AnalyticsPeriod.Year => "date_trunc('year', (NOW() AT TIME ZONE 'Asia/Kolkata'))::date",
            AnalyticsPeriod.Quarter => "date_trunc('quarter', (NOW() AT TIME ZONE 'Asia/Kolkata'))::date",
            _ => "date_trunc('month', (NOW() AT TIME ZONE 'Asia/Kolkata'))::date",
        };

        var kpis = await GetKpisAsync(tenantId, periodSql, ct);
        var weekly = await GetWeeklyVolumeAsync(tenantId, ct);
        var departments = await GetTopDepartmentsAsync(tenantId, periodSql, ct);
        var funnel = await GetFunnelAsync(tenantId, periodSql, ct);

        return new AnalyticsDto(kpis, weekly, departments, funnel);
    }

    // ---- KPIs ----------------------------------------------------------------------------------------

    private async Task<AnalyticsKpisDto> GetKpisAsync(Guid tenantId, string periodSql, CancellationToken ct)
    {
        // Period filter on booked_at (IST date) >= period start. revenue = SUM(consultation_fee) for
        // confirmed+completed; whatsappShare = whatsapp/total; noShowRate = no_show/total. All over the period.
        var rows = await db.Database.SqlQueryRaw<KpiRow>(
                $"""
                SELECT
                    COUNT(*)::int AS "Total",
                    COUNT(*) FILTER (WHERE b.booked_via = 'whatsapp')::int AS "Whatsapp",
                    COUNT(*) FILTER (WHERE b.status = 'no_show')::int AS "NoShow",
                    COALESCE(SUM(d.consultation_fee) FILTER (WHERE b.status IN ('confirmed','completed')), 0)::numeric AS "Revenue"
                FROM docslot.bookings b
                LEFT JOIN docslot.doctors d ON d.doctor_id = b.doctor_id
                WHERE b.tenant_id = @p0
                  AND (b.booked_at AT TIME ZONE 'Asia/Kolkata')::date >= {periodSql}
                """,
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);

        var r = rows.FirstOrDefault() ?? new KpiRow(0, 0, 0, 0m);
        var whatsappPct = Pct(r.Whatsapp, r.Total);
        var noShowPct = Pct(r.NoShow, r.Total);

        return new AnalyticsKpisDto(r.Total, whatsappPct, noShowPct, r.Revenue, DashboardContract.CurrencyCode);
    }

    // ---- Weekly volume (current week) ----------------------------------------------------------------

    private async Task<IReadOnlyList<WeeklyVolumeDto>> GetWeeklyVolumeAsync(Guid tenantId, CancellationToken ct)
    {
        // Bookings of the CURRENT week (Mon-anchored, IST), grouped by slot weekday; whatsapp vs other.
        // dow: 0=Sun..6=Sat (Postgres EXTRACT(DOW)). Week start = Monday of this IST week.
        var rows = await db.Database.SqlQueryRaw<WeekdayRow>(
                """
                SELECT
                    EXTRACT(DOW FROM s.slot_date)::int AS "Dow",
                    COUNT(*) FILTER (WHERE b.booked_via = 'whatsapp')::int AS "Whatsapp",
                    COUNT(*) FILTER (WHERE b.booked_via <> 'whatsapp')::int AS "Other"
                FROM docslot.bookings b
                JOIN docslot.time_slots s ON s.slot_id = b.slot_id
                WHERE b.tenant_id = @p0
                  AND s.slot_date >= (date_trunc('week', (NOW() AT TIME ZONE 'Asia/Kolkata'))::date)
                  AND s.slot_date <  (date_trunc('week', (NOW() AT TIME ZONE 'Asia/Kolkata'))::date + INTERVAL '7 days')
                GROUP BY EXTRACT(DOW FROM s.slot_date)
                """,
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);

        var byDow = rows.ToDictionary(x => x.Dow, x => x);

        // Emit Mon..Sun in order, zero-filling missing weekdays.
        string[] order = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
        int[] dowFor = [1, 2, 3, 4, 5, 6, 0]; // Postgres DOW for Mon..Sun
        var result = new List<WeeklyVolumeDto>(7);
        for (var i = 0; i < 7; i++)
        {
            byDow.TryGetValue(dowFor[i], out var row);
            result.Add(new WeeklyVolumeDto(order[i], row?.Whatsapp ?? 0, row?.Other ?? 0));
        }
        return result;
    }

    // ---- Top departments -----------------------------------------------------------------------------

    private async Task<IReadOnlyList<TopDepartmentDto>> GetTopDepartmentsAsync(Guid tenantId, string periodSql, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<DeptRow>(
                $"""
                SELECT dep.name AS "Name", COUNT(*)::int AS "Bookings"
                FROM docslot.bookings b
                JOIN docslot.departments dep ON dep.department_id = b.department_id
                WHERE b.tenant_id = @p0
                  AND b.department_id IS NOT NULL
                  AND (b.booked_at AT TIME ZONE 'Asia/Kolkata')::date >= {periodSql}
                GROUP BY dep.name
                ORDER BY COUNT(*) DESC, dep.name ASC
                LIMIT 6
                """,
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);

        return rows.Select(r => new TopDepartmentDto(r.Name, r.Bookings)).ToList();
    }

    // ---- WhatsApp conversational funnel --------------------------------------------------------------

    private async Task<IReadOnlyList<FunnelStageDto>> GetFunnelAsync(Guid tenantId, string periodSql, CancellationToken ct)
    {
        // Booking-derived funnel over WhatsApp bookings in the period. Stages are monotonic non-increasing:
        //   Started chat = distinct patients with a whatsapp booking
        //   Picked department = those with department_id
        //   Picked doctor     = those with doctor_id
        //   Picked slot       = those with slot_id
        //   Confirmed         = status in (confirmed, completed)
        // Counts use distinct patients at each stage (a sensible funnel population).
        var rows = await db.Database.SqlQueryRaw<FunnelRow>(
                $"""
                SELECT
                    COUNT(DISTINCT b.patient_id)::int AS "Started",
                    COUNT(DISTINCT b.patient_id) FILTER (WHERE b.department_id IS NOT NULL)::int AS "Department",
                    COUNT(DISTINCT b.patient_id) FILTER (WHERE b.doctor_id IS NOT NULL)::int AS "Doctor",
                    COUNT(DISTINCT b.patient_id) FILTER (WHERE b.slot_id IS NOT NULL)::int AS "Slot",
                    COUNT(DISTINCT b.patient_id) FILTER (WHERE b.status IN ('confirmed','completed'))::int AS "Confirmed"
                FROM docslot.bookings b
                WHERE b.tenant_id = @p0
                  AND b.booked_via = 'whatsapp'
                  AND (b.booked_at AT TIME ZONE 'Asia/Kolkata')::date >= {periodSql}
                """,
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);

        var r = rows.FirstOrDefault() ?? new FunnelRow(0, 0, 0, 0, 0);

        // Enforce monotonic non-increasing (defensive — the FILTERs above are subsets but the distinct-patient
        // population could in theory re-order; clamp each stage to the previous).
        var started = r.Started;
        var dept = Math.Min(r.Department, started);
        var doctor = Math.Min(r.Doctor, dept);
        var slot = Math.Min(r.Slot, doctor);
        var confirmed = Math.Min(r.Confirmed, slot);

        var basis = started;
        return
        [
            new FunnelStageDto("Started chat", started, Pct(started, basis)),
            new FunnelStageDto("Picked department", dept, Pct(dept, basis)),
            new FunnelStageDto("Picked doctor", doctor, Pct(doctor, basis)),
            new FunnelStageDto("Picked slot", slot, Pct(slot, basis)),
            new FunnelStageDto("Confirmed", confirmed, Pct(confirmed, basis)),
        ];
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static decimal Pct(int numerator, int denominator) =>
        denominator > 0 ? Math.Round((decimal)numerator * 100m / denominator, 2) : 0m;

    private sealed record KpiRow(int Total, int Whatsapp, int NoShow, decimal Revenue);
    private sealed record WeekdayRow(int Dow, int Whatsapp, int Other);
    private sealed record DeptRow(string Name, int Bookings);
    private sealed record FunnelRow(int Started, int Department, int Doctor, int Slot, int Confirmed);
}
