using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Commission;

/// <summary>
/// Persistence over <c>commission.attribution_claim_otps</c> (post-hoc broker-attribution claim consent).
/// Tenant isolation is enforced by RLS (file 07) — every statement runs inside the request/inbound command's
/// tenant-scoped UoW, so a pending claim is only ever visible to its own tenant. The code itself is never
/// stored; only a per-row salted SHA-256 digest (computed by <see cref="mediq.Application.Abstractions.IPostHocClaimService"/>).
/// Mirrors <see cref="mediq.Infrastructure.Docslot.WhatsApp.ConsentOtpStore"/>.
/// </summary>
public sealed class AttributionClaimOtpStore(PlatformDbContext db) : IAttributionClaimOtpStore
{
    public Task ExpireExistingPendingAsync(Guid tenantId, string patientPhone, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE commission.attribution_claim_otps
            SET status = 'expired', verified_at = @p2
            WHERE tenant_id = @p0 AND patient_phone = @p1 AND status = 'pending'
            """,
            new NpgsqlParameter("@p0", tenantId), new NpgsqlParameter("@p1", patientPhone),
            new NpgsqlParameter("@p2", nowUtc));

    public Task CreateAsync(ClaimOtpInsert r, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO commission.attribution_claim_otps
                (claim_otp_id, tenant_id, attribution_id, booking_id, broker_id, patient_phone, broker_phone,
                 claimed_relation, code_salt, code_hash, status, attempts, max_attempts, expires_at, created_at)
            VALUES (gen_random_uuid(), @p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, 'pending', 0, 5, @p9, @p10)
            """,
            new NpgsqlParameter("@p0", r.TenantId),
            new NpgsqlParameter("@p1", r.AttributionId),
            new NpgsqlParameter("@p2", r.BookingId),
            new NpgsqlParameter("@p3", r.BrokerId),
            new NpgsqlParameter("@p4", r.PatientPhone),
            new NpgsqlParameter("@p5", (object?)r.BrokerPhone ?? DBNull.Value),
            new NpgsqlParameter("@p6", (object?)r.ClaimedRelation ?? DBNull.Value),
            new NpgsqlParameter("@p7", r.CodeSalt),
            new NpgsqlParameter("@p8", r.CodeHash),
            new NpgsqlParameter("@p9", r.ExpiresAt),
            new NpgsqlParameter("@p10", r.NowUtc));

    public async Task<PendingClaimOtp?> GetPendingByPhoneAsync(
        Guid tenantId, string patientPhone, DateTime nowUtc, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<ClaimRow>(
                """
                SELECT claim_otp_id AS "ClaimOtpId", attribution_id AS "AttributionId", booking_id AS "BookingId",
                       broker_id AS "BrokerId", patient_phone AS "PatientPhone",
                       code_salt AS "CodeSalt", code_hash AS "CodeHash",
                       attempts AS "Attempts", max_attempts AS "MaxAttempts", expires_at AS "ExpiresAt"
                FROM commission.attribution_claim_otps
                WHERE tenant_id = @p0 AND patient_phone = @p1 AND status = 'pending'
                ORDER BY created_at DESC
                LIMIT 1
                """,
                new NpgsqlParameter("@p0", tenantId),
                new NpgsqlParameter("@p1", patientPhone))
            .ToListAsync(ct);

        var r = rows.FirstOrDefault();
        return r is null
            ? null
            : new PendingClaimOtp(r.ClaimOtpId, r.AttributionId, r.BookingId, r.BrokerId, r.PatientPhone,
                r.CodeSalt, r.CodeHash, r.Attempts, r.MaxAttempts,
                DateTime.SpecifyKind(r.ExpiresAt, DateTimeKind.Utc));
    }

    public Task SetStatusAsync(Guid claimOtpId, string status, DateTime? verifiedAtUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE commission.attribution_claim_otps SET status = @p1, verified_at = @p2 WHERE claim_otp_id = @p0",
            new NpgsqlParameter("@p0", claimOtpId),
            new NpgsqlParameter("@p1", status),
            new NpgsqlParameter("@p2", (object?)verifiedAtUtc ?? DBNull.Value));

    public Task IncrementAttemptsAsync(Guid claimOtpId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE commission.attribution_claim_otps SET attempts = attempts + 1 WHERE claim_otp_id = @p0",
            new NpgsqlParameter("@p0", claimOtpId));

    public async Task<int> ExpireStaleAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<IntResult>(
                "SELECT commission.expire_stale_attribution_claims() AS \"Value\"")
            .ToListAsync(ct);
        return rows.FirstOrDefault()?.Value ?? 0;
    }

    private sealed record IntResult(int Value);

    private sealed record ClaimRow(
        Guid ClaimOtpId, Guid AttributionId, Guid BookingId, Guid BrokerId, string PatientPhone,
        string CodeSalt, string CodeHash, short Attempts, short MaxAttempts, DateTime ExpiresAt);
}
