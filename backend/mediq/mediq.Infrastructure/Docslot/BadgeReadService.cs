using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Docslot;

/// <summary>
/// Computes navigation badge counts keyed by <c>platform.navigation_menus.badge_source</c>. The set of keys
/// is discovered from the schema (DISTINCT badge_source) so adding a menu badge in SQL never requires an
/// app change to APPEAR — only to be COMPUTED. Every known key is always present (0 when uncomputable);
/// counts are tenant-scoped aggregates (no PHI). "Today" is Asia/Kolkata.
/// </summary>
public sealed class BadgeReadService(PlatformDbContext db) : IBadgeReadService
{
    public async Task<IReadOnlyDictionary<string, int>> GetBadgeCountsAsync(Guid tenantId, CancellationToken ct)
    {
        // 1) Discover the declared badge sources from navigation (schema-driven).
        var sources = await db.Database.SqlQueryRaw<string>(
                """
                SELECT DISTINCT badge_source
                FROM platform.navigation_menus
                WHERE badge_source IS NOT NULL
                """)
            .ToListAsync(ct);

        // 2) Compute the counts we know how to compute, tenant-scoped, in ONE round-trip.
        var counts = await db.Database.SqlQueryRaw<CountsRow>(
                """
                SELECT
                    COUNT(*) FILTER (WHERE b.status = 'pending')::int AS "PendingBookings",
                    COUNT(*) FILTER (
                        WHERE (b.booked_at AT TIME ZONE 'Asia/Kolkata')::date = (NOW() AT TIME ZONE 'Asia/Kolkata')::date
                    )::int AS "TodayBookings"
                FROM docslot.bookings b
                WHERE b.tenant_id = @p0
                """,
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);

        var c = counts.FirstOrDefault() ?? new CountsRow(0, 0);

        // 3) Build the result: every declared source present; computed ones filled, the rest 0.
        var known = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["pending_bookings_count"] = c.PendingBookings,
            ["today_bookings_count"] = c.TodayBookings,
        };

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var source in sources)
            result[source] = known.TryGetValue(source, out var v) ? v : 0;

        // Guarantee the minimum contract key even if navigation has no such row in this DB.
        result.TryAdd("pending_bookings_count", c.PendingBookings);

        return result;
    }

    private sealed record CountsRow(int PendingBookings, int TodayBookings);
}
