using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Docslot.WhatsApp;

/// <summary>
/// Read-side menus for the conversational booking flow: active departments, a department's active doctors
/// (with consultation fee), and the earliest available future slots for a doctor. Tenant-scoped; no PHI
/// (clinic catalog + capacity only). Slot ordering matches the booking surface (earliest first, in
/// Asia/Kolkata wall-clock) so the option the patient picks is genuinely the soonest open time.
/// </summary>
public sealed class WhatsAppCatalogReadService(PlatformDbContext db) : IWhatsAppCatalogReadService
{
    public async Task<string?> GetTenantDisplayNameAsync(Guid tenantId, CancellationToken ct)
    {
        // platform.tenants is not RLS-gated by app.tenant_id (tenants ARE the tenant table); scope by id.
        var rows = await db.Database.SqlQueryRaw<NameRow>(
                "SELECT display_name AS \"Name\" FROM platform.tenants WHERE tenant_id = @p0",
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);
        return rows.FirstOrDefault()?.Name;
    }

    public async Task<IReadOnlyList<WaDepartment>> ListDepartmentsAsync(Guid tenantId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<DeptRow>(
                """
                SELECT department_id AS "DepartmentId", name AS "Name"
                FROM docslot.departments
                WHERE tenant_id = @p0 AND is_active = true
                ORDER BY display_order NULLS LAST, name
                """,
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);
        return rows.Select(r => new WaDepartment(r.DepartmentId, r.Name)).ToList();
    }

    public async Task<IReadOnlyList<WaDoctor>> ListDoctorsAsync(Guid tenantId, Guid departmentId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<DoctorRow>(
                """
                SELECT doctor_id AS "DoctorId", full_name AS "FullName", consultation_fee AS "ConsultationFee"
                FROM docslot.doctors
                WHERE tenant_id = @p0 AND department_id = @p1
                  AND is_active = true AND deleted_at IS NULL
                ORDER BY full_name
                """,
                new NpgsqlParameter("@p0", tenantId),
                new NpgsqlParameter("@p1", departmentId))
            .ToListAsync(ct);
        return rows.Select(r => new WaDoctor(r.DoctorId, r.FullName, r.ConsultationFee)).ToList();
    }

    public async Task<IReadOnlyList<WaSlot>> ListEarliestSlotsAsync(Guid tenantId, Guid doctorId, int take, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<SlotRow>(
                """
                SELECT slot_id AS "SlotId", slot_date AS "SlotDate", start_time AS "StartTime"
                FROM docslot.time_slots
                WHERE tenant_id = @p0 AND doctor_id = @p1
                  AND status = 'available' AND current_count < max_count
                  AND (slot_date + start_time) > (NOW() AT TIME ZONE 'Asia/Kolkata')
                ORDER BY slot_date, start_time
                LIMIT @p2
                """,
                new NpgsqlParameter("@p0", tenantId),
                new NpgsqlParameter("@p1", doctorId),
                new NpgsqlParameter("@p2", take))
            .ToListAsync(ct);
        return rows.Select(r => new WaSlot(r.SlotId, r.SlotDate, r.StartTime)).ToList();
    }

    private sealed record NameRow(string? Name);
    private sealed record DeptRow(Guid DepartmentId, string Name);
    private sealed record DoctorRow(Guid DoctorId, string FullName, decimal? ConsultationFee);
    private sealed record SlotRow(Guid SlotId, DateOnly SlotDate, TimeOnly StartTime);
}
