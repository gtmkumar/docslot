using System.Text.Json;
using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Domain.Commission;
using mediq.SharedDataModel.Docslot.Commission;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Commission;

/// <summary>
/// The attribution engine. Creates ONE attribution per (booking, broker) [UNIQUE], picks the
/// highest-priority matching active rule, calculates the commission (flat/percentage + caps/floors +
/// monthly per-broker cap + first_booking_only), scores fraud, and persists. Respects compliance:
/// <list type="bullet">
/// <item><b>Discount exclusivity</b>: if the booking carries a direct-booking discount, the DB trigger
/// rejects the insert and the repo surfaces <see cref="AttributionOnDiscountedBookingException"/> → 422.</item>
/// <item><b>PNDT</b>: brokers can't refer PNDT (DB CHECK); rules exclude PNDT (DB CHECK).</item>
/// <item><b>Blacklist</b>: blacklisted/inactive brokers earn nothing.</item>
/// </list>
/// </summary>
public sealed record CreateAttributionCommand(Guid TenantId, CreateAttributionRequest Request) : ICommand<AttributionResultDto>;

public sealed class CreateAttributionValidator : AbstractValidator<CreateAttributionCommand>
{
    private static readonly string[] Sources =
        ["referral_link", "broker_portal_booking", "whatsapp_template", "post_hoc_claim", "qr_scan", "manual_admin"];

    public CreateAttributionValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.BookingId).NotEmpty();
        RuleFor(x => x.Request.BrokerId).NotEmpty();
        RuleFor(x => x.Request.AttributionSource).Must(s => Sources.Contains(s)).WithMessage("Invalid attribution_source.");
    }
}

public sealed class CreateAttributionCommandHandler(
    IAttributionRepository attributions,
    IBrokerRepository brokers,
    ICommissionRuleRepository rules,
    IBrokerWalletRepository wallets,
    IFraudScorer fraud,
    IBrokerEventPublisher events,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx,
    IClock clock)
    : ICommandHandler<CreateAttributionCommand, AttributionResultDto>
{
    public async Task<AttributionResultDto> Handle(CreateAttributionCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var now = clock.UtcNow;

        // One attribution per (booking, broker).
        if (await attributions.ExistsAsync(req.BookingId, req.BrokerId, ct))
            throw new BusinessRuleException("This broker is already attributed to this booking.");

        // Broker must be able to earn (active, not blacklisted).
        var broker = await brokers.GetByIdAsync(req.BrokerId, ct) ?? throw new KeyNotFoundException("Broker not found.");
        if (!broker.CanEarn)
            throw new ForbiddenException("Broker is inactive or blacklisted; no attribution allowed.");
        if (!await brokers.IsLinkedToTenantAsync(broker.BrokerId, command.TenantId, ct))
            throw new ForbiddenException("Broker is not linked to this tenant.");

        // The booking + its value (and the direct-discount flag the DB trigger keys on).
        var bookingValue = await attributions.GetBookingValueAsync(req.BookingId, command.TenantId, ct)
            ?? throw new KeyNotFoundException("Booking not found in this tenant.");

        // Verification status by mechanism: link/portal = auto_verified; post-hoc = pending (patient confirms later).
        var verification = req.AttributionSource switch
        {
            "referral_link" or "broker_portal_booking" or "qr_scan" or "manual_admin" => "auto_verified",
            "post_hoc_claim" or "whatsapp_template" => "pending",
            _ => "pending",
        };

        // Pick the highest-priority matching active rule + calculate commission.
        var activeRules = await rules.GetActiveRulesAsync(command.TenantId, ct);
        CommissionRule? matched = null;
        decimal commission = 0m;
        foreach (var rule in activeRules.OrderByDescending(r => r.Priority))
        {
            if (!rule.Matches(broker.TierLevel, broker.BrokerType, req.ServiceType ?? bookingValue.ServiceType, bookingValue.AmountInr))
                continue;
            if (rule.FirstBookingOnly && await brokers.HasPriorAttributionAsync(broker.BrokerId, ct))
                continue;
            var earnedThisMonth = await attributions.BrokerEarnedThisMonthAsync(broker.BrokerId, command.TenantId, now, ct);
            commission = CommissionCalculator.Calculate(rule, bookingValue.AmountInr, earnedThisMonth);
            matched = rule;
            break;
        }

        // Campaign bonus ON TOP of the base commission (a tenant promo: "₹500 extra / 1.5× this month"). The DB
        // fn atomically reserves it against the campaign budget; it is folded into commission_amount_inr (so it
        // rides the same lifecycle + wallet buckets) and the split is recorded in source_metadata for the DB
        // refund-on-reversal trigger. Skipped for discounted bookings (the insert would be rejected anyway).
        var serviceType = req.ServiceType ?? bookingValue.ServiceType;
        CampaignBonusGrant? bonus = null;
        if (bookingValue.DirectDiscountInr <= 0m)
            bonus = await attributions.GrantCampaignBonusAsync(
                command.TenantId, broker.BrokerId, broker.TierLevel, broker.BrokerType, serviceType, commission, now, ct);
        var bonusInr = bonus?.BonusInr ?? 0m;
        var totalCommission = commission + bonusInr;

        var sourceMetadataJson = bonus is null ? null : JsonSerializer.Serialize(new
        {
            base_commission_inr = commission,
            campaign_id = bonus.CampaignId,
            campaign_bonus_inr = bonusInr,
        });

        // Fraud scoring (repeat_phone / rapid_burst / self_referral). Score >0.5 → flagged.
        var (score, flags) = await fraud.ScoreAsync(req.BookingId, req.BrokerId, ct);

        var attribution = Attribution.Create(
            command.TenantId, req.BookingId, req.BrokerId, req.AttributionSource, verification,
            matched?.RuleId, totalCommission, score, flags, now, sourceMetadataJson);

        // Persist — the DB exclusivity trigger throws (translated by the repo) for discounted bookings; the
        // campaign budget reservation above rolls back with it.
        await attributions.AddAsync(attribution, ct);

        // Credit the broker's PENDING wallet bucket (commission_status 'pending') with base + bonus; it moves to
        // earned when the booking completes (ICommissionLifecycleService), then to ready_to_pay at settlement, then paid.
        if (totalCommission > 0m)
        {
            await wallets.EnsureExistsAsync(req.BrokerId, ct);
            await wallets.ApplyAttributedAsync(req.BrokerId, totalCommission, now, ct);
        }

        await audit.RecordAsync(new AuditEntry(
            "attribute", "attribution", attribution.AttributionId, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Attribution via {req.AttributionSource}; commission ₹{totalCommission} (base ₹{commission}{(bonusInr > 0m ? $" + campaign bonus ₹{bonusInr}" : "")}); fraud {score:0.000} [{string.Join(',', flags)}]"), ct);

        // Integration event — IDs + amounts ONLY, NO patient PHI.
        await events.PublishAsync("commission.attribution.created", command.TenantId,
            new { attribution_id = attribution.AttributionId, broker_id = req.BrokerId, booking_id = req.BookingId, commission_inr = totalCommission, bonus_inr = bonusInr, campaign_id = bonus?.CampaignId }, ct);

        return new AttributionResultDto(
            attribution.AttributionId, req.BookingId, req.BrokerId, req.AttributionSource,
            verification, attribution.CommissionStatus, totalCommission, score, flags);
    }
}
