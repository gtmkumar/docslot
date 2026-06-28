using System.Security.Cryptography;
using mediq.Application.Abstractions;
using mediq.Domain.Docslot;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Docslot;

/// <summary>
/// Slot hold-on-selection with a 5-minute TTL (FR-BOOK-02), backed by the app-owned
/// <c>docslot.slot_holds</c> table. <see cref="HoldAsync"/> atomically verifies the slot is available
/// (status='available' AND current_count &lt; max_count AND no other LIVE hold) and inserts a hold in a
/// single conditional INSERT...SELECT so concurrent holders can't both succeed. Expired holds are ignored.
/// </summary>
public sealed class SlotHoldService(PlatformDbContext db) : ISlotHoldService
{
    public async Task<SlotHold> HoldAsync(
        Guid tenantId, Guid slotId, Guid doctorId, TimeSpan ttl, DateTime nowUtc, CancellationToken ct)
    {
        var holdId = Guid.CreateVersion7();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var expiresAt = nowUtc.Add(ttl);

        // Conditional insert: only succeeds if the slot is available, has capacity, BELONGS TO THIS DOCTOR
        // (consistency guard — a valid slot id paired with an unrelated doctor must not book), and has no
        // LIVE hold. All checked in one statement so concurrent holders can't both succeed.
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO docslot.slot_holds (hold_id, tenant_id, slot_id, hold_token, status, created_at, expires_at)
            SELECT @p0, @p1, @p2, @p3, 'held', NOW(), @p4
            FROM docslot.time_slots s
            WHERE s.slot_id = @p2
              AND s.tenant_id = @p1
              AND s.doctor_id = @p5
              AND s.status = 'available'
              AND s.current_count < s.max_count
              AND NOT EXISTS (
                  SELECT 1 FROM docslot.slot_holds h
                  WHERE h.slot_id = @p2 AND h.status = 'held' AND h.expires_at > NOW()
              )
            """,
            new NpgsqlParameter("@p0", holdId),
            new NpgsqlParameter("@p1", tenantId),
            new NpgsqlParameter("@p2", slotId),
            new NpgsqlParameter("@p3", token),
            new NpgsqlParameter("@p4", expiresAt),
            new NpgsqlParameter("@p5", doctorId));

        if (affected == 0)
            throw new SlotUnavailableException(slotId);

        return new SlotHold(holdId, slotId, token, expiresAt);
    }

    public async Task ConvertAsync(Guid holdId, Guid bookingId, DateTime nowUtc, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE docslot.slot_holds SET status = 'converted', booking_id = @p1 WHERE hold_id = @p0 AND status = 'held'",
            new NpgsqlParameter("@p0", holdId), new NpgsqlParameter("@p1", bookingId));

        // Consume slot capacity; mark booked when full.
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE docslot.time_slots
            SET current_count = current_count + 1,
                status = CASE WHEN current_count + 1 >= max_count THEN 'booked' ELSE status END
            WHERE slot_id = (SELECT slot_id FROM docslot.slot_holds WHERE hold_id = @p0)
            """,
            new NpgsqlParameter("@p0", holdId));
    }

    public Task ReleaseAsync(Guid holdId, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE docslot.slot_holds SET status = 'released' WHERE hold_id = @p0 AND status = 'held'",
            new NpgsqlParameter("@p0", holdId));

    public Task ReleaseSlotCapacityAsync(Guid slotId, DateTime nowUtc, CancellationToken ct) =>
        // Free the capacity a confirmed booking consumed (cancel/no-show): decrement (floored at 0) and
        // re-open a 'booked' slot to 'available'. A 'blocked' slot stays blocked.
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE docslot.time_slots
            SET current_count = GREATEST(current_count - 1, 0),
                status = CASE WHEN status = 'booked' THEN 'available' ELSE status END
            WHERE slot_id = @p0
            """,
            new NpgsqlParameter("@p0", slotId));

    public async Task<int> ExpireStaleHoldsAsync(DateTime nowUtc, CancellationToken ct)
    {
        // Via a SECURITY DEFINER fn: the maintenance worker has no per-request tenant context, and a plain
        // app-role UPDATE would match zero rows under the slot_holds RLS (app.tenant_id unset). The definer
        // runs as owner (bypasses RLS) and sweeps across all tenants.
        var rows = await db.Database.SqlQueryRaw<IntResult>(
            "SELECT docslot.expire_stale_slot_holds() AS \"Value\"").ToListAsync(ct);
        return rows.FirstOrDefault()?.Value ?? 0;
    }

    private sealed record IntResult(int Value);

    public async Task<bool> IsLiveAsync(Guid holdId, DateTime nowUtc, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<BoolResult>(
                "SELECT (status = 'held' AND expires_at > NOW()) AS \"Value\" FROM docslot.slot_holds WHERE hold_id = @p0",
                new NpgsqlParameter("@p0", holdId))
            .ToListAsync(ct);
        return rows.FirstOrDefault()?.Value ?? false;
    }

    private sealed record BoolResult(bool Value);
}
