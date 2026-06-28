using System.Security.Cryptography;
using System.Text.Json;
using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.Commission;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Commission;

/// <summary>Disputes + campaigns admin persistence.</summary>
public sealed class CommissionAdminRepository(PlatformDbContext db) : ICommissionAdminRepository
{
    public async Task<Guid> RaiseDisputeAsync(Guid tenantId, RaiseDisputeRequest r, Guid? byUserId, DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO commission.attribution_disputes
                (dispute_id, attribution_id, tenant_id, raised_by, raised_by_user_id, raised_at, dispute_reason, description, status, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, 'open', @p5, @p5)
            """,
            P(("@p0", id), ("@p1", r.AttributionId), ("@p2", tenantId), ("@p3", r.RaisedBy),
              ("@p4", (object?)byUserId ?? DBNull.Value), ("@p5", nowUtc), ("@p6", r.DisputeReason), ("@p7", r.Description)));
        return id;
    }

    public async Task<Guid> ResolveDisputeAsync(Guid disputeId, ResolveDisputeRequest r, Guid byUserId, DateTime nowUtc, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<Guid>(
                """
                UPDATE commission.attribution_disputes
                SET status=@p1, resolved_at=@p2, resolved_by_user_id=@p3, resolution_notes=@p4, resolution_amount_adjustment_inr=@p5
                WHERE dispute_id=@p0
                RETURNING attribution_id AS "Value"
                """,
                P(("@p0", disputeId), ("@p1", r.Status), ("@p2", nowUtc), ("@p3", byUserId),
                  ("@p4", (object?)r.ResolutionNotes ?? DBNull.Value), ("@p5", (object?)r.AmountAdjustmentInr ?? DBNull.Value)))
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    public async Task<Guid> CreateCampaignAsync(Guid tenantId, CreateCampaignRequest r, Guid? byUserId, DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO commission.broker_campaigns
                (campaign_id, tenant_id, campaign_name, bonus_type, bonus_value, starts_at, ends_at,
                 is_active, total_budget_inr, created_at, updated_at, created_by_user_id)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, true, @p7, @p8, @p8, @p9)
            """,
            P(("@p0", id), ("@p1", tenantId), ("@p2", r.CampaignName), ("@p3", r.BonusType),
              ("@p4", (object?)r.BonusValue ?? DBNull.Value), ("@p5", r.StartsAt.UtcDateTime), ("@p6", r.EndsAt.UtcDateTime),
              ("@p7", (object?)r.TotalBudgetInr ?? DBNull.Value), ("@p8", nowUtc), ("@p9", (object?)byUserId ?? DBNull.Value)));
        return id;
    }

    public async Task<IReadOnlyList<DisputeDto>> ListDisputesAsync(Guid tenantId, CancellationToken ct) =>
        (await db.Database.SqlQueryRaw<DisputeRow>(
                """
                SELECT d.dispute_id AS "DisputeId", d.attribution_id AS "AttributionId",
                       COALESCE(bk.booking_number, '') AS "BookingRef", COALESCE(b.full_name, '') AS "BrokerName",
                       d.raised_by AS "RaisedBy", d.dispute_reason AS "DisputeReason", d.status AS "Status", d.raised_at AS "RaisedAt"
                FROM commission.attribution_disputes d
                JOIN commission.attributions a ON a.attribution_id = d.attribution_id
                LEFT JOIN commission.brokers b ON b.broker_id = a.broker_id
                LEFT JOIN docslot.bookings bk ON bk.booking_id = a.booking_id
                WHERE d.tenant_id=@p0 ORDER BY d.raised_at DESC
                """,
                P(("@p0", tenantId)))
            .ToListAsync(ct))
            .Select(r => new DisputeDto(r.DisputeId, r.AttributionId, r.BookingRef, r.BrokerName, r.RaisedBy, r.DisputeReason, r.Status,
                new DateTimeOffset(DateTime.SpecifyKind(r.RaisedAt, DateTimeKind.Utc))))
            .ToList();

    public async Task<IReadOnlyList<CampaignDto>> ListCampaignsAsync(Guid tenantId, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<CampaignRow>(
                """
                SELECT campaign_id AS "CampaignId", campaign_name AS "CampaignName", bonus_type AS "BonusType",
                       bonus_value AS "BonusValue", is_active AS "IsActive", total_budget_inr AS "TotalBudgetInr", spent_so_far_inr AS "SpentSoFarInr"
                FROM commission.broker_campaigns WHERE tenant_id=@p0 ORDER BY starts_at DESC
                """,
                P(("@p0", tenantId)))
            .Select(r => new CampaignDto(r.CampaignId, r.CampaignName, r.BonusType, r.BonusValue, r.IsActive, r.TotalBudgetInr, r.SpentSoFarInr))
            .ToListAsync(ct);

    private static object[] P(params (string Name, object Value)[] ps) => ps.Select(p => (object)new NpgsqlParameter(p.Name, p.Value)).ToArray();
    private sealed record DisputeRow(Guid DisputeId, Guid AttributionId, string BookingRef, string BrokerName, string RaisedBy, string DisputeReason, string Status, DateTime RaisedAt);
    private sealed record CampaignRow(Guid CampaignId, string CampaignName, string BonusType, decimal? BonusValue, bool IsActive, decimal? TotalBudgetInr, decimal SpentSoFarInr);
}

/// <summary>Referral link generation (BRK-codes).</summary>
public sealed class ReferralLinkRepository(PlatformDbContext db) : IReferralLinkRepository
{
    public async Task<ReferralLinkDto> CreateAsync(Guid brokerId, CreateReferralLinkRequest req, DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var shortCode = $"BRK-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToUpperInvariant()}";
        var targetUrl = $"https://wa.me/?ref={shortCode}";
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO commission.referral_links (link_id, broker_id, tenant_id, short_code, target_url, target_doctor_id, campaign_name, is_active, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, true, @p7, @p7)
            """,
            P(("@p0", id), ("@p1", brokerId), ("@p2", (object?)req.TenantId ?? DBNull.Value), ("@p3", shortCode),
              ("@p4", targetUrl), ("@p5", (object?)req.TargetDoctorId ?? DBNull.Value), ("@p6", (object?)req.CampaignName ?? DBNull.Value), ("@p7", nowUtc)));
        return new ReferralLinkDto(id, shortCode, targetUrl, 0, 0, true);
    }

    public async Task<IReadOnlyList<ReferralLinkDto>> ListByBrokerAsync(Guid brokerId, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<LinkRow>(
                """
                SELECT link_id AS "LinkId", short_code AS "ShortCode", target_url AS "TargetUrl",
                       click_count AS "ClickCount", conversion_count AS "ConversionCount", is_active AS "IsActive"
                FROM commission.referral_links WHERE broker_id=@p0 ORDER BY created_at DESC
                """,
                P(("@p0", brokerId)))
            .Select(r => new ReferralLinkDto(r.LinkId, r.ShortCode, r.TargetUrl, r.ClickCount, r.ConversionCount, r.IsActive))
            .ToListAsync(ct);

    private static object[] P(params (string Name, object Value)[] ps) => ps.Select(p => (object)new NpgsqlParameter(p.Name, p.Value)).ToArray();
    private sealed record LinkRow(Guid LinkId, string ShortCode, string? TargetUrl, int ClickCount, int ConversionCount, bool IsActive);
}

/// <summary>Resolves a user's own broker id (commission.brokers.user_id) at login → the IDOR-safe broker_id claim.</summary>
public sealed class BrokerIdentityResolver(PlatformDbContext db) : IBrokerIdentityResolver
{
    public async Task<Guid?> ResolveBrokerIdAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<Guid>(
                "SELECT broker_id AS \"Value\" FROM commission.brokers WHERE user_id = @p0 AND blacklisted_at IS NULL LIMIT 1",
                new NpgsqlParameter("@p0", userId))
            .ToListAsync(ct);
        return rows.Count > 0 ? rows[0] : null;
    }
}

/// <summary>
/// Publishes commission integration events through the slice-02 webhook pipeline. Payloads carry IDs +
/// amounts ONLY — NEVER patient PHI (verified by test).
/// </summary>
public sealed class BrokerEventPublisher(IWebhookPublisher webhooks, ICurrentUserContext ctx, IClock clock) : IBrokerEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task PublishAsync(string eventType, Guid tenantId, object payload, CancellationToken ct)
    {
        var envelope = new { event_type = eventType, tenant_id = tenantId, occurred_at = clock.UtcNow, data = payload };
        var evt = new IntegrationEvent(
            Guid.CreateVersion7(), eventType, tenantId, JsonSerializer.Serialize(envelope, JsonOptions), ctx.CorrelationId, clock.UtcNow);
        return webhooks.PublishAsync(evt, ct);
    }
}
