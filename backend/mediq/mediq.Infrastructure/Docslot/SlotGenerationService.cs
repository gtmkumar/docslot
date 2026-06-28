using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Docslot;

/// <summary>
/// Materializes bookable <c>docslot.time_slots</c> via the <c>docslot.generate_time_slots</c> SECURITY DEFINER
/// function (which derives tenant_id from the doctor and is idempotent). The single-doctor call backs the
/// staff "generate" endpoint; the rolling-horizon call (one set-based statement over all active doctors)
/// backs the nightly materializer worker.
/// </summary>
public sealed class SlotGenerationService(PlatformDbContext db) : ISlotGenerationService
{
    public async Task<int> GenerateAsync(Guid doctorId, DateOnly fromDate, DateOnly toDate, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<IntRow>(
            "SELECT docslot.generate_time_slots(@p0, @p1, @p2) AS \"Value\"",
            new NpgsqlParameter("@p0", doctorId),
            new NpgsqlParameter("@p1", fromDate),
            new NpgsqlParameter("@p2", toDate)).ToListAsync(ct);
        return rows.FirstOrDefault()?.Value ?? 0;
    }

    public async Task<int> GenerateRollingHorizonAsync(DateOnly fromDate, int horizonDays, CancellationToken ct)
    {
        var toDate = fromDate.AddDays(Math.Max(0, horizonDays));
        // One statement generates for every active doctor; the definer fn handles per-doctor tenant scoping.
        var rows = await db.Database.SqlQueryRaw<IntRow>(
            """
            SELECT COALESCE(SUM(docslot.generate_time_slots(d.doctor_id, @p0, @p1)), 0)::int AS "Value"
            FROM docslot.doctors d
            WHERE d.deleted_at IS NULL AND d.is_active = true
            """,
            new NpgsqlParameter("@p0", fromDate),
            new NpgsqlParameter("@p1", toDate)).ToListAsync(ct);
        return rows.FirstOrDefault()?.Value ?? 0;
    }

    private sealed record IntRow(int Value);
}
