using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Docslot;

/// <summary>
/// Persistence over <c>docslot.bookings</c> for the proactive no-show backfill (slice 16). A background worker
/// has NO tenant/request context, so it cannot satisfy the RLS predicate on <c>docslot.bookings</c> directly —
/// it reaches the rows through two SECURITY DEFINER functions (the rls-cross-tenant-worker pattern). Because the
/// functions run as their owner and expose ONLY non-PHI features (ids + lead-time / slot-hour / on-behalf), this
/// store is a PLAIN app-role call: there is deliberately NO <c>BeginTenantScopeAsync</c> / GUC set here (the
/// definer functions are the sanctioned cross-tenant entry point; EXECUTE on them is granted to docslot_app).
/// </summary>
public sealed class NoShowBackfillStore(PlatformDbContext db) : INoShowBackfillStore
{
    public async Task<IReadOnlyList<DueNoShowBooking>> ListDueAsync(int windowHours, int limit, CancellationToken ct)
    {
        // Cross-tenant, NON-PHI projection from the SECURITY DEFINER function: upcoming pending/confirmed
        // bookings (slot within the window) that have not yet been scored. PascalCase aliases bind the record.
        var rows = await db.Database.SqlQueryRaw<DueNoShowBooking>(
                """
                SELECT booking_id AS "BookingId", tenant_id AS "TenantId", lead_time_days AS "LeadTimeDays",
                       slot_hour AS "SlotHour", is_behalf AS "IsBehalf"
                FROM docslot.list_due_noshow_bookings(@p0, @p1)
                """,
                new NpgsqlParameter("@p0", windowHours),
                new NpgsqlParameter("@p1", limit))
            .ToListAsync(ct);

        return rows;
    }

    public Task MarkPredictedAsync(Guid bookingId, CancellationToken ct) =>
        // Idempotency marker: sets no_show_predicted_at = NOW() via the definer function, so the next scan
        // excludes this booking (it is no longer "due").
        db.Database.ExecuteSqlRawAsync(
            "SELECT docslot.mark_noshow_predicted(@p0)",
            [new NpgsqlParameter("@p0", bookingId)],
            ct);
}
