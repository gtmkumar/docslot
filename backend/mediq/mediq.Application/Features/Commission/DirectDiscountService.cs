using mediq.Application.Abstractions;
using mediq.Domain.Commission;

namespace mediq.Application.Features.Commission;

/// <summary>
/// Computes the direct-patient discount for a broker-less booking and writes it. Picks the highest-priority
/// active commission rule that matches the booking's service + value (broker tier/type filters are irrelevant
/// with no broker), computes the would-be commission from that rule, and gives <c>DirectDiscountPct</c> of it
/// back to the patient as <c>direct_discount_inr</c>. The discount makes the booking ineligible for any later
/// broker attribution (DB trigger) — closing the double-dip loophole.
/// </summary>
public sealed class DirectDiscountService(
    IAttributionRepository attributions, ICommissionRuleRepository rules) : IDirectDiscountService
{
    public async Task<decimal> ApplyAsync(Guid tenantId, Guid bookingId, DateTime nowUtc, CancellationToken ct)
    {
        var bv = await attributions.GetBookingValueAsync(bookingId, tenantId, ct);
        if (bv is null || bv.AmountInr <= 0m) return 0m;

        var active = await rules.GetActiveRulesAsync(tenantId, ct);
        var rule = active
            .OrderByDescending(r => r.Priority)
            .FirstOrDefault(r => r.MatchesDirect(bv.ServiceType, bv.AmountInr));
        if (rule is null) return 0m;

        var wouldBeCommission = CommissionCalculator.Calculate(rule, bv.AmountInr, brokerEarnedThisMonth: 0m);
        var discount = Math.Round(wouldBeCommission * rule.DirectDiscountPct / 100m, 2);
        if (discount <= 0m) return 0m;

        await attributions.WriteDirectDiscountAsync(bookingId, tenantId, discount, rule.RuleId, nowUtc, ct);
        return discount;
    }
}
