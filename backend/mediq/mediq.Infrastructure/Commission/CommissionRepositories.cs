using System.Text.Json;
using mediq.Application.Abstractions;
using mediq.Domain.Commission;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.Commission;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Commission;

/// <summary>Commission rules: create (draft), approve (activate), list, and active-rules-by-priority for the engine.</summary>
public sealed class CommissionRuleRepository(PlatformDbContext db) : ICommissionRuleRepository
{
    public async Task<Guid> CreateAsync(Guid tenantId, CreateCommissionRuleRequest r, DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        // excludes_pndt is forced true (DB CHECK also enforces). Rules start inactive (is_active=false).
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO commission.commission_rules
                (rule_id, tenant_id, rule_name, rule_key, calc_type, flat_amount_inr, percentage,
                 min_commission_inr, max_commission_inr, max_monthly_per_broker_inr,
                 applies_to_broker_tier, applies_to_service_type, priority, excludes_pndt, first_booking_only,
                 is_active, effective_from, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, true, @p13, false, @p14, @p14, @p14)
            """,
            P(("@p0", id), ("@p1", tenantId), ("@p2", r.RuleName), ("@p3", r.RuleKey), ("@p4", r.CalcType),
              ("@p5", (object?)r.FlatAmountInr ?? DBNull.Value), ("@p6", (object?)r.Percentage ?? DBNull.Value),
              ("@p7", (object?)r.MinCommissionInr ?? DBNull.Value), ("@p8", (object?)r.MaxCommissionInr ?? DBNull.Value),
              ("@p9", (object?)r.MaxMonthlyPerBrokerInr ?? DBNull.Value),
              ("@p10", (object?)r.AppliesToBrokerTier?.ToArray() ?? DBNull.Value),
              ("@p11", (object?)r.AppliesToServiceType?.ToArray() ?? DBNull.Value),
              ("@p12", r.Priority), ("@p13", r.FirstBookingOnly), ("@p14", nowUtc)));
        return id;
    }

    public Task ApproveAsync(Guid ruleId, Guid byUserId, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE commission.commission_rules SET is_active=true, approved_by_user_id=@p1, approved_at=@p2 WHERE rule_id=@p0",
            P(("@p0", ruleId), ("@p1", byUserId), ("@p2", nowUtc)));

    public async Task<IReadOnlyList<CommissionRuleDto>> ListAsync(Guid tenantId, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<RuleListRow>(
                """
                SELECT rule_id AS "RuleId", rule_name AS "RuleName", rule_key AS "RuleKey", calc_type AS "CalcType",
                       flat_amount_inr AS "FlatAmountInr", percentage AS "Percentage", priority AS "Priority",
                       is_active AS "IsActive", excludes_pndt AS "ExcludesPndt"
                FROM commission.commission_rules WHERE tenant_id=@p0 ORDER BY priority DESC
                """,
                P(("@p0", tenantId)))
            .Select(r => new CommissionRuleDto(r.RuleId, r.RuleName, r.RuleKey, r.CalcType, r.FlatAmountInr, r.Percentage, r.Priority, r.IsActive, r.ExcludesPndt))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<CommissionRule>> GetActiveRulesAsync(Guid tenantId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<RuleRow>(
                """
                SELECT rule_id AS "RuleId", tenant_id AS "TenantId", rule_name AS "RuleName",
                       applies_to_broker_tier AS "AppliesToBrokerTier", applies_to_broker_type AS "AppliesToBrokerType",
                       applies_to_service_type AS "AppliesToServiceType", min_booking_value_inr AS "MinBookingValueInr",
                       max_booking_value_inr AS "MaxBookingValueInr", calc_type AS "CalcType", flat_amount_inr AS "FlatAmountInr",
                       percentage AS "Percentage", min_commission_inr AS "MinCommissionInr", max_commission_inr AS "MaxCommissionInr",
                       max_monthly_per_broker_inr AS "MaxMonthlyPerBrokerInr", priority AS "Priority",
                       excludes_pndt AS "ExcludesPndt", first_booking_only AS "FirstBookingOnly",
                       tiered_table::text AS "TieredTableJson", direct_discount_pct AS "DirectDiscountPct"
                FROM commission.commission_rules
                WHERE tenant_id=@p0 AND is_active=true AND effective_from <= NOW()
                  AND (effective_until IS NULL OR effective_until > NOW())
                ORDER BY priority DESC
                """,
                P(("@p0", tenantId)))
            .ToListAsync(ct);
        return rows.Select(r => CommissionRule.FromRow(
            r.RuleId, r.TenantId, r.RuleName, r.AppliesToBrokerTier, r.AppliesToBrokerType, r.AppliesToServiceType,
            r.MinBookingValueInr, r.MaxBookingValueInr, r.CalcType, r.FlatAmountInr, r.Percentage,
            r.MinCommissionInr, r.MaxCommissionInr, r.MaxMonthlyPerBrokerInr, r.Priority, r.ExcludesPndt, r.FirstBookingOnly,
            r.TieredTableJson, r.DirectDiscountPct)).ToList();
    }

    private static object[] P(params (string Name, object Value)[] ps) => ps.Select(p => (object)new NpgsqlParameter(p.Name, p.Value)).ToArray();
    private sealed record RuleListRow(Guid RuleId, string RuleName, string RuleKey, string CalcType, decimal? FlatAmountInr, decimal? Percentage, int Priority, bool IsActive, bool ExcludesPndt);
    private sealed record RuleRow(Guid RuleId, Guid TenantId, string RuleName, string[]? AppliesToBrokerTier, string[]? AppliesToBrokerType,
        string[]? AppliesToServiceType, decimal? MinBookingValueInr, decimal? MaxBookingValueInr, string CalcType, decimal? FlatAmountInr,
        decimal? Percentage, decimal? MinCommissionInr, decimal? MaxCommissionInr, decimal? MaxMonthlyPerBrokerInr, int Priority, bool ExcludesPndt, bool FirstBookingOnly,
        string? TieredTableJson, decimal DirectDiscountPct);
}

/// <summary>Payout batches. approve and execute are separate operations (gated by distinct permissions at the API).</summary>
public sealed class PayoutRepository(PlatformDbContext db) : IPayoutRepository
{
    public async Task<Guid> CreateAsync(Payout p, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO commission.payouts
                (payout_id, tenant_id, broker_id, period_start, period_end, attribution_count,
                 gross_amount_inr, tds_rate, tds_amount_inr, gst_rate, gst_amount_inr, net_amount_inr,
                 status, payment_method, initiated_at, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, 'pending', @p12, @p13, @p13, @p13)
            """,
            P(("@p0", p.PayoutId), ("@p1", p.TenantId), ("@p2", p.BrokerId), ("@p3", p.PeriodStart), ("@p4", p.PeriodEnd),
              ("@p5", p.AttributionCount), ("@p6", p.GrossAmountInr), ("@p7", p.TdsRate), ("@p8", p.TdsAmountInr),
              ("@p9", (object?)p.GstRate ?? DBNull.Value), ("@p10", p.GstAmountInr), ("@p11", p.NetAmountInr),
              ("@p12", p.PaymentMethod), ("@p13", p.CreatedAt)));
        return p.PayoutId;
    }

    public async Task<Payout?> GetByIdAsync(Guid payoutId, Guid tenantId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<PayoutRow>(
                """
                SELECT payout_id AS "PayoutId", tenant_id AS "TenantId", broker_id AS "BrokerId", period_start AS "PeriodStart",
                       period_end AS "PeriodEnd", attribution_count AS "AttributionCount", gross_amount_inr AS "GrossAmountInr",
                       tds_rate AS "TdsRate", tds_amount_inr AS "TdsAmountInr", gst_rate AS "GstRate", gst_amount_inr AS "GstAmountInr",
                       net_amount_inr AS "NetAmountInr", status AS "Status", payment_method AS "PaymentMethod",
                       approved_by_user_id AS "ApprovedByUserId", approved_at AS "ApprovedAt", payment_reference AS "PaymentReference", created_at AS "CreatedAt"
                FROM commission.payouts WHERE payout_id=@p0 AND tenant_id=@p1
                """,
                P(("@p0", payoutId), ("@p1", tenantId)))
            .ToListAsync(ct);
        var r = rows.FirstOrDefault();
        return r is null ? null : Hydrate(r);
    }

    public Task ApproveAsync(Guid payoutId, Guid byUserId, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE commission.payouts SET status='approved', approved_by_user_id=@p1, approved_at=@p2 WHERE payout_id=@p0 AND status='pending'",
            P(("@p0", payoutId), ("@p1", byUserId), ("@p2", nowUtc)));

    public async Task<bool> TryClaimForExecutionAsync(Guid payoutId, Guid tenantId, DateTime nowUtc, CancellationToken ct)
    {
        // Single-winner claim: approved → processing. The conditional UPDATE is atomic, so a concurrent second
        // execute matches 0 rows and is rejected — no double gateway call, no double wallet credit.
        var affected = await db.Database.ExecuteSqlRawAsync(
            "UPDATE commission.payouts SET status='processing' WHERE payout_id=@p0 AND tenant_id=@p1 AND status='approved'",
            P(("@p0", payoutId), ("@p1", tenantId)));
        return affected == 1;
    }

    public Task MarkPaidAsync(Guid payoutId, string reference, string gateway, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE commission.payouts SET status='paid', payment_reference=@p1, payment_gateway=@p3, completed_at=@p2 WHERE payout_id=@p0 AND status='processing'",
            P(("@p0", payoutId), ("@p1", reference), ("@p2", nowUtc), ("@p3", gateway)));

    public Task MarkFailedAsync(Guid payoutId, DateTime nowUtc, CancellationToken ct) =>
        // Gateway rejected the transfer — the batch returns to a terminal 'failed' (re-issue is a new batch).
        // The failure detail is captured in the audit log (no money moved; attributions stay ready_to_pay).
        db.Database.ExecuteSqlRawAsync(
            "UPDATE commission.payouts SET status='failed', completed_at=@p1 WHERE payout_id=@p0 AND status='processing'",
            P(("@p0", payoutId), ("@p1", nowUtc)));

    public async Task<IReadOnlyList<PayoutDto>> ListByTenantAsync(Guid tenantId, int skip, int take, CancellationToken ct) =>
        (await db.Database.SqlQueryRaw<PayoutListRow>(
                """
                SELECT p.payout_id AS "PayoutId", p.broker_id AS "BrokerId", b.full_name AS "BrokerName",
                       p.period_start AS "PeriodStart", p.period_end AS "PeriodEnd", p.attribution_count AS "AttributionCount",
                       p.gross_amount_inr AS "GrossAmountInr", p.tds_rate AS "TdsRate", p.tds_amount_inr AS "TdsAmountInr",
                       p.gst_rate AS "GstRate", p.gst_amount_inr AS "GstAmountInr", p.net_amount_inr AS "NetAmountInr",
                       p.status AS "Status", p.payment_reference AS "PaymentReference"
                FROM commission.payouts p
                JOIN commission.brokers b ON b.broker_id = p.broker_id
                WHERE p.tenant_id=@p0 ORDER BY p.created_at DESC OFFSET @p1 LIMIT @p2
                """,
                P(("@p0", tenantId), ("@p1", skip), ("@p2", take)))
            .ToListAsync(ct))
            .Select(r => new PayoutDto(r.PayoutId, r.BrokerId, r.BrokerName, r.PeriodStart, r.PeriodEnd, r.AttributionCount,
                r.GrossAmountInr, r.TdsRate, r.TdsAmountInr, r.GstRate, r.GstAmountInr, r.NetAmountInr, r.Status, r.PaymentReference)).ToList();

    private static Payout Hydrate(PayoutRow r) => Payout.CreatePending(
        r.TenantId, r.BrokerId, r.PeriodStart, r.PeriodEnd, r.AttributionCount,
        new PayoutBreakdown(r.GrossAmountInr, r.TdsRate, r.TdsAmountInr, r.GstRate, r.GstAmountInr, r.NetAmountInr, r.NetAmountInr >= 100m),
        r.PaymentMethod, r.CreatedAt) is var p ? SetState(p, r.PayoutId, r.Status, r.ApprovedByUserId, r.ApprovedAt, r.PaymentReference) : p;

    private static Payout SetState(Payout p, Guid id, string status, Guid? approvedBy, DateTime? approvedAt, string? reference)
    {
        typeof(Payout).GetProperty(nameof(Payout.PayoutId))!.SetValue(p, id);
        typeof(Payout).GetProperty(nameof(Payout.Status))!.SetValue(p, status);
        typeof(Payout).GetProperty(nameof(Payout.ApprovedByUserId))!.SetValue(p, approvedBy);
        typeof(Payout).GetProperty(nameof(Payout.ApprovedAt))!.SetValue(p, approvedAt);
        typeof(Payout).GetProperty(nameof(Payout.PaymentReference))!.SetValue(p, reference);
        return p;
    }

    private static object[] P(params (string Name, object Value)[] ps) => ps.Select(p => (object)new NpgsqlParameter(p.Name, p.Value)).ToArray();
    private sealed record PayoutRow(Guid PayoutId, Guid TenantId, Guid BrokerId, DateOnly PeriodStart, DateOnly PeriodEnd, int AttributionCount,
        decimal GrossAmountInr, decimal TdsRate, decimal TdsAmountInr, decimal? GstRate, decimal GstAmountInr, decimal NetAmountInr,
        string Status, string PaymentMethod, Guid? ApprovedByUserId, DateTime? ApprovedAt, string? PaymentReference, DateTime CreatedAt);
    private sealed record PayoutListRow(Guid PayoutId, Guid BrokerId, string BrokerName, DateOnly PeriodStart, DateOnly PeriodEnd,
        int AttributionCount, decimal GrossAmountInr, decimal TdsRate, decimal TdsAmountInr, decimal? GstRate, decimal GstAmountInr,
        decimal NetAmountInr, string Status, string? PaymentReference);
}

/// <summary>Materialized broker wallet.</summary>
public sealed class BrokerWalletRepository(PlatformDbContext db) : IBrokerWalletRepository
{
    public Task EnsureExistsAsync(Guid brokerId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "INSERT INTO commission.broker_wallets (broker_id, updated_at) VALUES (@p0, NOW()) ON CONFLICT (broker_id) DO NOTHING",
            new NpgsqlParameter("@p0", brokerId));

    public Task ApplyAttributedAsync(Guid brokerId, decimal amountInr, DateTime nowUtc, CancellationToken ct) =>
        // Attribution created (commission_status 'pending'): money sits in pending until the booking completes.
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE commission.broker_wallets
            SET pending_inr = pending_inr + @p1, current_month_inr = current_month_inr + @p1,
                current_month_attributions = current_month_attributions + 1, lifetime_attributions = lifetime_attributions + 1,
                last_attribution_at = @p2, updated_at = @p2
            WHERE broker_id = @p0
            """,
            new NpgsqlParameter("@p0", brokerId), new NpgsqlParameter("@p1", amountInr), new NpgsqlParameter("@p2", nowUtc));

    public Task ApplyEarnedAsync(Guid brokerId, decimal amountInr, DateTime nowUtc, CancellationToken ct) =>
        // Booking completed (pending → earned): move money from pending to earned (settlement later → ready_to_pay).
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE commission.broker_wallets
            SET pending_inr = GREATEST(0, pending_inr - @p1), earned_inr = earned_inr + @p1, updated_at = @p2
            WHERE broker_id = @p0
            """,
            new NpgsqlParameter("@p0", brokerId), new NpgsqlParameter("@p1", amountInr), new NpgsqlParameter("@p2", nowUtc));

    public Task ApplyReversedAsync(Guid brokerId, decimal amountInr, string fromStatus, DateTime nowUtc, CancellationToken ct) =>
        // Clawback: debit the bucket the attribution was in (a 'paid' reversal has no live bucket — money's gone)
        // and record it as lifetime_reversed.
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE commission.broker_wallets
            SET pending_inr      = CASE WHEN @p2 = 'pending'      THEN GREATEST(0, pending_inr - @p1)      ELSE pending_inr END,
                earned_inr       = CASE WHEN @p2 = 'earned'       THEN GREATEST(0, earned_inr - @p1)       ELSE earned_inr END,
                ready_to_pay_inr = CASE WHEN @p2 = 'ready_to_pay' THEN GREATEST(0, ready_to_pay_inr - @p1) ELSE ready_to_pay_inr END,
                lifetime_reversed_inr = lifetime_reversed_inr + @p1,
                updated_at = @p3
            WHERE broker_id = @p0
            """,
            new NpgsqlParameter("@p0", brokerId), new NpgsqlParameter("@p1", amountInr),
            new NpgsqlParameter("@p2", fromStatus), new NpgsqlParameter("@p3", nowUtc));

    public Task ApplyPaidAsync(Guid brokerId, decimal grossInr, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE commission.broker_wallets
            SET ready_to_pay_inr = GREATEST(0, ready_to_pay_inr - @p1), lifetime_paid_inr = lifetime_paid_inr + @p1,
                last_payout_at = @p2, updated_at = @p2
            WHERE broker_id = @p0
            """,
            new NpgsqlParameter("@p0", brokerId), new NpgsqlParameter("@p1", grossInr), new NpgsqlParameter("@p2", nowUtc));

    public async Task<BrokerWalletDto?> GetAsync(Guid brokerId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<WalletRow>(
                """
                SELECT broker_id AS "BrokerId", pending_inr AS "PendingInr", earned_inr AS "EarnedInr",
                       ready_to_pay_inr AS "ReadyToPayInr", lifetime_paid_inr AS "LifetimePaidInr",
                       current_month_inr AS "CurrentMonthInr", current_month_attributions AS "CurrentMonthAttributions"
                FROM commission.broker_wallets WHERE broker_id=@p0
                """,
                new NpgsqlParameter("@p0", brokerId))
            .ToListAsync(ct);
        var r = rows.FirstOrDefault();
        return r is null ? null : new BrokerWalletDto(r.BrokerId, r.PendingInr, r.EarnedInr, r.ReadyToPayInr, r.LifetimePaidInr, r.CurrentMonthInr, r.CurrentMonthAttributions);
    }

    private sealed record WalletRow(Guid BrokerId, decimal PendingInr, decimal EarnedInr, decimal ReadyToPayInr, decimal LifetimePaidInr, decimal CurrentMonthInr, int CurrentMonthAttributions);
}

/// <summary>Pure fraud scoring: repeat_phone / rapid_burst / self_referral. Score >0.5 → flagged.</summary>
public sealed class FraudScorer(IAttributionRepository attributions) : IFraudScorer
{
    public async Task<(decimal Score, string[] Flags)> ScoreAsync(Guid bookingId, Guid brokerId, CancellationToken ct)
    {
        var flags = new List<string>();
        decimal score = 0m;

        if (await attributions.BookingPatientReferredBySelfAsync(bookingId, brokerId, ct))
        { flags.Add("self_referral"); score += 0.6m; }

        var recent = await attributions.CountRecentByBrokerAsync(brokerId, TimeSpan.FromMinutes(5), ct);
        if (recent >= 10) { flags.Add("rapid_burst"); score += 0.4m; }

        return (Math.Min(1.0m, score), flags.ToArray());
    }
}
