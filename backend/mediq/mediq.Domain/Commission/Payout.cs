namespace mediq.Domain.Commission;

/// <summary>
/// Pure Indian-tax payout math: gross → TDS (5% u/s 194H) → GST (18% added if the broker is GST-registered)
/// → net. Enforces the ₹100 minimum payout floor. No side effects — the engine persists the result.
/// </summary>
public static class PayoutCalculator
{
    public const decimal MinimumPayoutInr = 100m;
    public const decimal DefaultTdsRate = 5.00m;     // Section 194H
    public const decimal GstRate = 18.00m;

    /// <summary>
    /// Computes a payout breakdown. TDS is deducted from gross. GST (18%) is ADDED for GST-registered
    /// brokers (they collect it and remit it; the facility pays it on top). Net = gross - TDS (+ GST if registered).
    /// </summary>
    public static PayoutBreakdown Compute(decimal grossInr, bool brokerGstRegistered)
    {
        var tds = Math.Round(grossInr * DefaultTdsRate / 100m, 2);
        decimal? gstRate = brokerGstRegistered ? GstRate : null;
        var gst = brokerGstRegistered ? Math.Round(grossInr * GstRate / 100m, 2) : 0m;
        var net = Math.Round(grossInr - tds + gst, 2);
        return new PayoutBreakdown(grossInr, DefaultTdsRate, tds, gstRate, gst, net, net >= MinimumPayoutInr);
    }
}

public sealed record PayoutBreakdown(
    decimal GrossInr,
    decimal TdsRate,
    decimal TdsInr,
    decimal? GstRate,
    decimal GstInr,
    decimal NetInr,
    bool MeetsMinimum);

/// <summary>A batch payout for one broker for one period (maps to <c>commission.payouts</c>).</summary>
public sealed class Payout
{
    public Guid PayoutId { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid BrokerId { get; private set; }
    public DateOnly PeriodStart { get; private set; }
    public DateOnly PeriodEnd { get; private set; }
    public int AttributionCount { get; private set; }
    public decimal GrossAmountInr { get; private set; }
    public decimal TdsRate { get; private set; }
    public decimal TdsAmountInr { get; private set; }
    public decimal? GstRate { get; private set; }
    public decimal GstAmountInr { get; private set; }
    public decimal NetAmountInr { get; private set; }
    public string Status { get; private set; } = "pending";   // pending→approved→processing→paid / failed / on_hold / reversed
    public string PaymentMethod { get; private set; } = "upi";
    public Guid? ApprovedByUserId { get; private set; }
    public DateTime? ApprovedAt { get; private set; }
    public string? PaymentReference { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private Payout() { }

    public static Payout CreatePending(
        Guid tenantId, Guid brokerId, DateOnly periodStart, DateOnly periodEnd, int attributionCount,
        PayoutBreakdown b, string paymentMethod, DateTime nowUtc)
        => new()
        {
            PayoutId = Guid.CreateVersion7(),
            TenantId = tenantId, BrokerId = brokerId, PeriodStart = periodStart, PeriodEnd = periodEnd,
            AttributionCount = attributionCount, GrossAmountInr = b.GrossInr, TdsRate = b.TdsRate,
            TdsAmountInr = b.TdsInr, GstRate = b.GstRate, GstAmountInr = b.GstInr, NetAmountInr = b.NetInr,
            Status = "pending", PaymentMethod = paymentMethod, CreatedAt = nowUtc,
        };

    public bool IsApproved => Status == "approved";
    public bool CanExecute => Status == "approved";   // execution requires prior approval
}
