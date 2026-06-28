using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Docslot.WhatsApp;

/// <summary>
/// Persistence over <c>docslot.booking_consent_otps</c> (behalf-booking patient consent). Tenant isolation is
/// enforced by RLS (file 09) — every statement runs inside the inbound command's tenant-scoped UoW, so a
/// pending consent is only ever visible to its own tenant. The code itself is never stored; only a per-row
/// salted SHA-256 digest (computed by <see cref="mediq.Application.Abstractions.IPatientConsentService"/>).
/// </summary>
public sealed class ConsentOtpStore(PlatformDbContext db) : IConsentOtpStore
{
    public Task ExpireExistingPendingAsync(Guid tenantId, string patientPhone, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE docslot.booking_consent_otps
            SET status = 'expired'
            WHERE tenant_id = @p0 AND patient_phone = @p1 AND status = 'pending'
            """,
            new NpgsqlParameter("@p0", tenantId), new NpgsqlParameter("@p1", patientPhone));

    public Task CreateAsync(ConsentOtpInsert r, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO docslot.booking_consent_otps
                (consent_otp_id, tenant_id, booking_id, patient_phone, booker_phone, relation,
                 code_salt, code_hash, status, attempts, max_attempts, expires_at, created_at)
            VALUES (gen_random_uuid(), @p0, @p1, @p2, @p3, @p4, @p5, @p6, 'pending', 0, 5, @p7, @p8)
            """,
            new NpgsqlParameter("@p0", r.TenantId),
            new NpgsqlParameter("@p1", r.BookingId),
            new NpgsqlParameter("@p2", r.PatientPhone),
            new NpgsqlParameter("@p3", r.BookerPhone),
            new NpgsqlParameter("@p4", r.Relation),
            new NpgsqlParameter("@p5", r.CodeSalt),
            new NpgsqlParameter("@p6", r.CodeHash),
            new NpgsqlParameter("@p7", r.ExpiresAt),
            new NpgsqlParameter("@p8", r.NowUtc));

    public async Task<PendingConsentOtp?> GetPendingByPhoneAsync(
        Guid tenantId, string patientPhone, DateTime nowUtc, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<OtpRow>(
                """
                SELECT consent_otp_id AS "ConsentOtpId", booking_id AS "BookingId",
                       patient_phone AS "PatientPhone", booker_phone AS "BookerPhone", relation AS "Relation",
                       code_salt AS "CodeSalt", code_hash AS "CodeHash",
                       attempts AS "Attempts", max_attempts AS "MaxAttempts", expires_at AS "ExpiresAt"
                FROM docslot.booking_consent_otps
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
            : new PendingConsentOtp(r.ConsentOtpId, r.BookingId, r.PatientPhone, r.BookerPhone, r.Relation,
                r.CodeSalt, r.CodeHash, r.Attempts, r.MaxAttempts,
                DateTime.SpecifyKind(r.ExpiresAt, DateTimeKind.Utc));
    }

    public Task SetStatusAsync(Guid consentOtpId, string status, DateTime? verifiedAtUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE docslot.booking_consent_otps SET status = @p1, verified_at = @p2 WHERE consent_otp_id = @p0",
            new NpgsqlParameter("@p0", consentOtpId),
            new NpgsqlParameter("@p1", status),
            new NpgsqlParameter("@p2", (object?)verifiedAtUtc ?? DBNull.Value));

    public Task IncrementAttemptsAsync(Guid consentOtpId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE docslot.booking_consent_otps SET attempts = attempts + 1 WHERE consent_otp_id = @p0",
            new NpgsqlParameter("@p0", consentOtpId));

    public async Task<int> ExpireStaleAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<IntResult>(
                "SELECT docslot.expire_stale_consent_otps() AS \"Value\"")
            .ToListAsync(ct);
        return rows.FirstOrDefault()?.Value ?? 0;
    }

    private sealed record IntResult(int Value);

    private sealed record OtpRow(
        Guid ConsentOtpId, Guid BookingId, string PatientPhone, string BookerPhone, string Relation,
        string CodeSalt, string CodeHash, short Attempts, short MaxAttempts, DateTime ExpiresAt);
}
