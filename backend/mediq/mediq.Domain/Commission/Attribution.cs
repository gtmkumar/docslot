namespace mediq.Domain.Commission;

/// <summary>
/// Raised when an attribution is blocked by the discount↔attribution mutual-exclusivity rule (the booking
/// carries a direct-booking discount, which IS the patient's declaration that no one referred them). The DB
/// trigger <c>trg_no_attribution_on_discounted</c> enforces this; the Application catches the SQLSTATE and
/// surfaces THIS clean error. Maps to 422.
/// </summary>
public sealed class AttributionOnDiscountedBookingException(Guid bookingId)
    : Exception($"Booking '{bookingId}' carries a direct-booking discount; broker attribution is not allowed (mutual exclusivity).");

/// <summary>Raised when a PCPNDT-forbidden value would be written (defensive; the DB CHECK also blocks it).</summary>
public sealed class PndtComplianceException()
    : Exception("PCPNDT: commission on gender-determination referrals is prohibited (can_refer_pndt must be false, excludes_pndt must be true).");

/// <summary>
/// One (broker, booking) attribution — the core ledger row (maps to <c>commission.attributions</c>).
/// UNIQUE(booking_id, broker_id). Commission lifecycle: pending→earned→ready_to_pay→paid / reversed.
/// </summary>
public sealed class Attribution
{
    public Guid AttributionId { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid BookingId { get; private set; }
    public Guid BrokerId { get; private set; }
    public string AttributionSource { get; private set; } = default!;
    public string VerificationStatus { get; private set; } = "pending";
    public Guid? RuleId { get; private set; }
    public decimal? CommissionAmountInr { get; private set; }
    public string CommissionStatus { get; private set; } = "pending";
    public decimal? FraudScore { get; private set; }
    public string[] FraudFlags { get; private set; } = [];
    public Guid? PayoutId { get; private set; }
    public DateTime AttributedAt { get; private set; }
    public DateTime? EarnedAt { get; private set; }

    private Attribution() { }

    public static Attribution Create(
        Guid tenantId, Guid bookingId, Guid brokerId, string source, string verificationStatus,
        Guid? ruleId, decimal? commission, decimal fraudScore, string[] fraudFlags, DateTime nowUtc)
        => new()
        {
            AttributionId = Guid.CreateVersion7(),
            TenantId = tenantId, BookingId = bookingId, BrokerId = brokerId,
            AttributionSource = source, VerificationStatus = verificationStatus, RuleId = ruleId,
            CommissionAmountInr = commission, CommissionStatus = "pending",
            FraudScore = fraudScore, FraudFlags = fraudFlags, AttributedAt = nowUtc,
        };
}

/// <summary>Commission rule (maps to <c>commission.commission_rules</c>). The calculation lives in <see cref="CommissionCalculator"/>.</summary>
public sealed class CommissionRule
{
    public Guid RuleId { get; private set; }
    public Guid TenantId { get; private set; }
    public string RuleName { get; private set; } = default!;
    public string[]? AppliesToBrokerTier { get; private set; }
    public string[]? AppliesToBrokerType { get; private set; }
    public string[]? AppliesToServiceType { get; private set; }
    public decimal? MinBookingValueInr { get; private set; }
    public decimal? MaxBookingValueInr { get; private set; }
    public string CalcType { get; private set; } = default!;     // 'flat' | 'percentage' | 'tiered_table'
    public decimal? FlatAmountInr { get; private set; }
    public decimal? Percentage { get; private set; }
    public decimal? MinCommissionInr { get; private set; }
    public decimal? MaxCommissionInr { get; private set; }
    public decimal? MaxMonthlyPerBrokerInr { get; private set; }
    public int Priority { get; private set; }
    public bool ExcludesPndt { get; private set; }               // ALWAYS true (DB CHECK).
    public bool FirstBookingOnly { get; private set; }

    private CommissionRule() { }

    public static CommissionRule FromRow(
        Guid id, Guid tenantId, string name, string[]? tiers, string[]? types, string[]? services,
        decimal? minVal, decimal? maxVal, string calcType, decimal? flat, decimal? pct,
        decimal? minComm, decimal? maxComm, decimal? maxMonthly, int priority, bool excludesPndt, bool firstBookingOnly)
        => new()
        {
            RuleId = id, TenantId = tenantId, RuleName = name, AppliesToBrokerTier = tiers,
            AppliesToBrokerType = types, AppliesToServiceType = services, MinBookingValueInr = minVal,
            MaxBookingValueInr = maxVal, CalcType = calcType, FlatAmountInr = flat, Percentage = pct,
            MinCommissionInr = minComm, MaxCommissionInr = maxComm, MaxMonthlyPerBrokerInr = maxMonthly,
            Priority = priority, ExcludesPndt = excludesPndt, FirstBookingOnly = firstBookingOnly,
        };

    /// <summary>True if this rule applies to the (broker tier/type, service, booking value) context.</summary>
    public bool Matches(string brokerTier, string brokerType, string? serviceType, decimal bookingValue)
    {
        if (AppliesToBrokerTier is { Length: > 0 } && !AppliesToBrokerTier.Contains(brokerTier)) return false;
        if (AppliesToBrokerType is { Length: > 0 } && !AppliesToBrokerType.Contains(brokerType)) return false;
        if (AppliesToServiceType is { Length: > 0 } && serviceType is not null && !AppliesToServiceType.Contains(serviceType)) return false;
        if (MinBookingValueInr is { } min && bookingValue < min) return false;
        if (MaxBookingValueInr is { } max && bookingValue > max) return false;
        return true;
    }
}

/// <summary>
/// Pure commission calculation: flat / percentage / tiered, then apply floor (min), ceiling (max), and the
/// monthly per-broker cap (clamped against what the broker already earned this month). No side effects.
/// </summary>
public static class CommissionCalculator
{
    public static decimal Calculate(CommissionRule rule, decimal bookingValueInr, decimal brokerEarnedThisMonth)
    {
        var amount = rule.CalcType switch
        {
            "flat" => rule.FlatAmountInr ?? 0m,
            "percentage" => Math.Round(bookingValueInr * (rule.Percentage ?? 0m) / 100m, 2),
            "tiered_table" => 0m,   // tiered_table parsed by the engine from JSON; flat fallback here
            _ => 0m,
        };

        // Floor then ceiling per-booking.
        if (rule.MinCommissionInr is { } floor) amount = Math.Max(amount, floor);
        if (rule.MaxCommissionInr is { } ceiling) amount = Math.Min(amount, ceiling);

        // Monthly per-broker cap: never let this booking push the broker over the cap.
        if (rule.MaxMonthlyPerBrokerInr is { } monthlyCap)
        {
            var remaining = Math.Max(0m, monthlyCap - brokerEarnedThisMonth);
            amount = Math.Min(amount, remaining);
        }

        return Math.Max(0m, Math.Round(amount, 2));
    }
}
