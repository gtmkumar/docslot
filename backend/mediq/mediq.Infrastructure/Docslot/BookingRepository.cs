using mediq.Application.Abstractions;
using mediq.Domain.Docslot;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Docslot;

/// <summary>
/// Write-side access to <c>docslot.bookings</c> (the aggregate). Tenant-scoped reads; insert flushes
/// immediately so the DB trigger assigns booking_number and FK-referencing follow-ups (hold convert, OPD
/// token) see the row. Action mutations are committed by the UnitOfWork behavior (and the DB status-history
/// trigger logs the transition — we never insert history rows manually).
/// </summary>
public sealed class BookingRepository(PlatformDbContext db) : IBookingRepository
{
    public Task<Booking?> GetByIdAsync(Guid bookingId, Guid tenantId, CancellationToken ct) =>
        db.Bookings.FirstOrDefaultAsync(b => b.BookingId == bookingId && b.TenantId == tenantId, ct);

    public async Task<string?> AddAndSaveAsync(Booking booking, CancellationToken ct)
    {
        await db.Bookings.AddAsync(booking, ct);
        await db.SaveChangesAsync(ct);                 // trigger assigns booking_number on insert
        await db.Entry(booking).ReloadAsync(ct);       // pull the trigger-assigned booking_number back
        return booking.BookingNumber;
    }

    public async Task<string?> GetBookingNumberAsync(Guid bookingId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<NumberRow>(
                "SELECT booking_number AS \"Number\" FROM docslot.bookings WHERE booking_id = @p0",
                new NpgsqlParameter("@p0", bookingId))
            .ToListAsync(ct);
        return rows.FirstOrDefault()?.Number;
    }

    private sealed record NumberRow(string? Number);
}

/// <summary>Issues sequential OPD queue tokens per (doctor, date) into <c>docslot.opd_tokens</c>.</summary>
public sealed class OpdTokenService(PlatformDbContext db) : IOpdTokenService
{
    public async Task<int> IssueAsync(Guid tenantId, Guid bookingId, Guid doctorId, DateOnly date, DateTime nowUtc, CancellationToken ct)
    {
        // Allocate the next token number for the doctor/date and insert atomically.
        var rows = await db.Database.SqlQueryRaw<TokenRow>(
                """
                INSERT INTO docslot.opd_tokens (token_id, booking_id, doctor_id, tenant_id, token_date, token_number, status)
                SELECT gen_random_uuid(), @p0, @p1, @p2, @p3,
                       COALESCE(MAX(token_number), 0) + 1, 'waiting'
                FROM docslot.opd_tokens WHERE doctor_id = @p1 AND token_date = @p3
                RETURNING token_number AS "TokenNumber"
                """,
                new NpgsqlParameter("@p0", bookingId),
                new NpgsqlParameter("@p1", doctorId),
                new NpgsqlParameter("@p2", tenantId),
                new NpgsqlParameter("@p3", date))
            .ToListAsync(ct);
        return rows.First().TokenNumber;
    }

    private sealed record TokenRow(int TokenNumber);
}

/// <summary>
/// Appends a purpose-of-use declaration to <c>platform.purpose_of_use_log</c> (DPDP). Written on a
/// DEDICATED connection: clinical READS are query handlers that run inside the read transaction (which is
/// rolled back to clear the tenant-RLS GUC), so the purpose record must NOT be in that transaction — DPDP
/// accountability must persist regardless of the read transaction's disposal.
/// </summary>
public sealed class PurposeOfUseWriter(IDedicatedConnectionFactory connections) : IPurposeOfUseWriter
{
    public async Task RecordAsync(PurposeOfUseEntry e, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO platform.purpose_of_use_log
                (log_id, user_id, tenant_id, accessed_resource_type, accessed_resource_id, declared_purpose,
                 purpose_notes, is_break_glass, break_glass_reason, accessed_at, review_required)
            VALUES (gen_random_uuid(), @p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, NOW(), @p6)
            """, conn);
        cmd.Parameters.AddWithValue("@p0", e.UserId);
        cmd.Parameters.AddWithValue("@p1", e.TenantId);
        cmd.Parameters.AddWithValue("@p2", e.ResourceType);
        cmd.Parameters.AddWithValue("@p3", e.ResourceId);
        cmd.Parameters.AddWithValue("@p4", e.DeclaredPurpose);
        cmd.Parameters.AddWithValue("@p5", (object?)e.PurposeNotes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p6", e.IsBreakGlass);
        cmd.Parameters.AddWithValue("@p7", (object?)e.BreakGlassReason ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
