using mediq.Application.Abstractions;
using mediq.Domain.Commission;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.Commission;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Commission;

/// <summary>
/// Broker identity (platform-level by phone) + tenant linkage + KYC. PAN is stored as a ciphertext envelope
/// (the handler encrypts before calling Create). NEVER returns PAN. Care Partner is the customer-facing term.
/// </summary>
public sealed class BrokerRepository(PlatformDbContext db) : IBrokerRepository
{
    private const string CarePartnerLabel = "Care Partner";

    public Task<Broker?> GetByPhoneAsync(string phone, CancellationToken ct) =>
        db.Brokers.FirstOrDefaultAsync(b => b.Phone == phone, ct);

    public Task<Broker?> GetByIdAsync(Guid brokerId, CancellationToken ct) =>
        db.Brokers.FirstOrDefaultAsync(b => b.BrokerId == brokerId, ct);

    public async Task<Guid> CreateAsync(Broker broker, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO commission.brokers
                (broker_id, phone, full_name, email, broker_type, pan_number, gst_number,
                 tier_level, payout_method, is_active, can_refer_pndt, requires_consent_for_phi, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, 'basic', 'upi', false, false, true, @p7, @p7)
            """,
            P(("@p0", broker.BrokerId), ("@p1", broker.Phone), ("@p2", broker.FullName),
              ("@p3", (object?)broker.Email ?? DBNull.Value), ("@p4", broker.BrokerType),
              ("@p5", (object?)broker.PanNumberEnc ?? DBNull.Value), ("@p6", (object?)broker.GstNumber ?? DBNull.Value),
              ("@p7", broker.CreatedAt)));
        return broker.BrokerId;
    }

    public Task LinkToTenantAsync(Guid brokerId, Guid tenantId, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO commission.broker_tenant_links (link_id, broker_id, tenant_id, is_active, activated_at, created_at, updated_at)
            VALUES (gen_random_uuid(), @p0, @p1, true, @p2, @p2, @p2)
            ON CONFLICT (broker_id, tenant_id) DO NOTHING
            """,
            P(("@p0", brokerId), ("@p1", tenantId), ("@p2", nowUtc)));

    public async Task SetActiveAsync(Guid brokerId, Guid tenantId, bool isActive, Guid? byUserId, DateTime nowUtc, CancellationToken ct)
    {
        // Per-tenant link status + the broker's global activation flag.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE commission.broker_tenant_links SET is_active = @p2 WHERE broker_id = @p0 AND tenant_id = @p1",
            P(("@p0", brokerId), ("@p1", tenantId), ("@p2", isActive)));
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE commission.brokers SET is_active = @p1,
                activated_at = CASE WHEN @p1 THEN @p2 ELSE activated_at END,
                activated_by_user_id = CASE WHEN @p1 THEN @p3 ELSE activated_by_user_id END
            WHERE broker_id = @p0
            """,
            P(("@p0", brokerId), ("@p1", isActive), ("@p2", nowUtc), ("@p3", (object?)byUserId ?? DBNull.Value)));
    }

    public Task BlacklistAsync(Guid brokerId, string reason, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE commission.brokers SET blacklisted_at = @p1, blacklist_reason = @p2, is_active = false WHERE broker_id = @p0",
            P(("@p0", brokerId), ("@p1", nowUtc), ("@p2", reason)));

    public Task<bool> IsLinkedToTenantAsync(Guid brokerId, Guid tenantId, CancellationToken ct) =>
        db.Database.SqlQueryRaw<bool>(
                "SELECT EXISTS(SELECT 1 FROM commission.broker_tenant_links WHERE broker_id=@p0 AND tenant_id=@p1 AND is_active) AS \"Value\"",
                P(("@p0", brokerId), ("@p1", tenantId)))
            .FirstAsync(ct);

    public async Task<IReadOnlyList<BrokerDto>> ListByTenantAsync(Guid tenantId, int skip, int take, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<BrokerListRow>(
                """
                SELECT b.broker_id AS "BrokerId", b.phone AS "Phone", b.full_name AS "FullName", b.email AS "Email",
                       b.broker_type AS "BrokerType", b.tier_level AS "TierLevel", b.pan_verified AS "PanVerified",
                       b.gst_verified AS "GstVerified", b.is_active AS "IsActive", (b.blacklisted_at IS NOT NULL) AS "IsBlacklisted"
                FROM commission.brokers b
                JOIN commission.broker_tenant_links l ON l.broker_id = b.broker_id
                WHERE l.tenant_id = @p0
                ORDER BY b.full_name OFFSET @p1 LIMIT @p2
                """,
                P(("@p0", tenantId), ("@p1", skip), ("@p2", take)))
            .ToListAsync(ct);
        // NOTE: pan_number is intentionally NOT selected — PAN is never returned. Phone is MASKED here (DPDP)
        // — the raw broker phone is never serialised into a list payload.
        return rows.Select(r => new BrokerDto(
            r.BrokerId, mediq.Infrastructure.Docslot.PhoneMasker.Mask(r.Phone), r.FullName, r.Email, r.BrokerType, r.TierLevel,
            r.PanVerified, r.GstVerified, r.IsActive, r.IsBlacklisted, CarePartnerLabel)).ToList();
    }

    public Task<bool> GstRegisteredAsync(Guid brokerId, CancellationToken ct) =>
        db.Database.SqlQueryRaw<bool>(
                "SELECT (gst_number IS NOT NULL AND gst_verified) AS \"Value\" FROM commission.brokers WHERE broker_id=@p0",
                P(("@p0", brokerId)))
            .FirstOrDefaultAsync(ct);

    public Task<bool> HasPriorAttributionAsync(Guid brokerId, CancellationToken ct) =>
        db.Database.SqlQueryRaw<bool>(
                "SELECT EXISTS(SELECT 1 FROM commission.attributions WHERE broker_id=@p0) AS \"Value\"",
                P(("@p0", brokerId)))
            .FirstAsync(ct);

    private static object[] P(params (string Name, object Value)[] ps) =>
        ps.Select(p => (object)new NpgsqlParameter(p.Name, p.Value)).ToArray();

    private sealed record BrokerListRow(Guid BrokerId, string Phone, string FullName, string? Email, string BrokerType,
        string TierLevel, bool PanVerified, bool GstVerified, bool IsActive, bool IsBlacklisted);
}
