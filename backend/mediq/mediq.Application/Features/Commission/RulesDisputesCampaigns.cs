using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Commission;

namespace mediq.Application.Features.Commission;

// ---- Commission rules ----------------------------------------------------------------------------

public sealed record CreateRuleCommand(Guid TenantId, CreateCommissionRuleRequest Request) : ICommand<Guid>;

public sealed class CreateRuleValidator : AbstractValidator<CreateRuleCommand>
{
    public CreateRuleValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.RuleName).NotEmpty();
        RuleFor(x => x.Request.RuleKey).NotEmpty().Matches("^[a-z0-9_]+$");
        RuleFor(x => x.Request.CalcType).Must(t => t is "flat" or "percentage" or "tiered_table");

        // Server-side range guards (a hardened API must not rely on the client's min={0}/max={100} inputs).
        // The .NET CommissionCalculator treats null=unset and 0=a real cap distinctly, so negatives are the
        // only genuinely invalid values here; percentage is bounded 0..100.
        RuleFor(x => x.Request.FlatAmountInr).GreaterThanOrEqualTo(0m).When(x => x.Request.FlatAmountInr.HasValue);
        RuleFor(x => x.Request.Percentage).InclusiveBetween(0m, 100m).When(x => x.Request.Percentage.HasValue);
        RuleFor(x => x.Request.MinCommissionInr).GreaterThanOrEqualTo(0m).When(x => x.Request.MinCommissionInr.HasValue);
        RuleFor(x => x.Request.MaxCommissionInr).GreaterThanOrEqualTo(0m).When(x => x.Request.MaxCommissionInr.HasValue);
        RuleFor(x => x.Request.MaxMonthlyPerBrokerInr).GreaterThanOrEqualTo(0m).When(x => x.Request.MaxMonthlyPerBrokerInr.HasValue);
        RuleFor(x => x.Request.Priority).GreaterThanOrEqualTo(0);
        // A floor above the ceiling is contradictory: the calculator applies the floor first (Math.Max) then the
        // ceiling (Math.Min), so an inverted min>max would clamp every payout to the ceiling — reject it when both set.
        RuleFor(x => x.Request)
            .Must(r => !(r.MinCommissionInr.HasValue && r.MaxCommissionInr.HasValue) || r.MinCommissionInr!.Value <= r.MaxCommissionInr!.Value)
            .WithMessage("MinCommissionInr must not exceed MaxCommissionInr.");
    }
}

public sealed class CreateRuleCommandHandler(
    ICommissionRuleRepository rules, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<CreateRuleCommand, Guid>
{
    public async Task<Guid> Handle(CreateRuleCommand command, CancellationToken ct)
    {
        var id = await rules.CreateAsync(command.TenantId, command.Request, clock.UtcNow, ct);
        await audit.RecordAsync(new AuditEntry(
            "create_rule", "commission_rule", id, command.Request.RuleKey, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: "Commission rule created (draft, excludes PNDT)"), ct);
        return id;
    }
}

public sealed record ApproveRuleCommand(Guid TenantId, Guid RuleId) : ICommand<Unit>;

public sealed class ApproveRuleCommandHandler(
    ICommissionRuleRepository rules, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<ApproveRuleCommand>
{
    public async Task<Unit> Handle(ApproveRuleCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        await rules.ApproveAsync(command.RuleId, userId, clock.UtcNow, ct);
        await audit.RecordAsync(new AuditEntry(
            "approve_rule", "commission_rule", command.RuleId, null, userId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: "Commission rule activated"), ct);
        return Unit.Value;
    }
}

public sealed record ListRulesQuery(Guid TenantId) : IQuery<IReadOnlyList<CommissionRuleDto>>;

public sealed class ListRulesQueryHandler(ICommissionRuleRepository rules)
    : IQueryHandler<ListRulesQuery, IReadOnlyList<CommissionRuleDto>>
{
    public Task<IReadOnlyList<CommissionRuleDto>> Handle(ListRulesQuery q, CancellationToken ct) => rules.ListAsync(q.TenantId, ct);
}

// ---- Disputes ------------------------------------------------------------------------------------

public sealed record RaiseDisputeCommand(Guid TenantId, RaiseDisputeRequest Request) : ICommand<Guid>;

public sealed class RaiseDisputeValidator : AbstractValidator<RaiseDisputeCommand>
{
    public RaiseDisputeValidator()
    {
        RuleFor(x => x.Request.AttributionId).NotEmpty();
        RuleFor(x => x.Request.Description).NotEmpty();
    }
}

public sealed class RaiseDisputeCommandHandler(
    ICommissionAdminRepository admin, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<RaiseDisputeCommand, Guid>
{
    public async Task<Guid> Handle(RaiseDisputeCommand command, CancellationToken ct)
    {
        var id = await admin.RaiseDisputeAsync(command.TenantId, command.Request, ctx.UserId, clock.UtcNow, ct);
        await audit.RecordAsync(new AuditEntry(
            "raise_dispute", "attribution_dispute", id, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: $"Dispute: {command.Request.DisputeReason}"), ct);
        return id;
    }
}

public sealed record ResolveDisputeCommand(Guid TenantId, ResolveDisputeRequest Request) : ICommand<Unit>;

public sealed class ResolveDisputeCommandHandler(
    ICommissionAdminRepository admin, IAttributionRepository attributions, IBrokerWalletRepository wallets,
    IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<ResolveDisputeCommand>
{
    public async Task<Unit> Handle(ResolveDisputeCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var now = clock.UtcNow;
        var req = command.Request;

        var attributionId = await admin.ResolveDisputeAsync(req.DisputeId, req, userId, now, ct);

        // Clawback: when the tenant wins (or a negative adjustment is recorded), reverse the disputed
        // attribution and debit the broker wallet bucket it was sitting in (idempotent — a paid attribution
        // records lifetime_reversed without driving a live bucket negative).
        var isClawback = req.Status == "resolved_tenant_wins"
                         || (req.AmountAdjustmentInr is { } adj && adj < 0m);
        if (isClawback && attributionId != Guid.Empty)
        {
            var reversed = await attributions.ReverseOneAsync(attributionId, command.TenantId, now, ct);
            if (reversed is { Amount: > 0m } rv)
                await wallets.ApplyReversedAsync(rv.BrokerId, rv.Amount, rv.FromStatus, now, ct);
        }

        await audit.RecordAsync(new AuditEntry(
            "resolve_dispute", "attribution_dispute", req.DisputeId, null, userId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Resolved: {req.Status}{(isClawback ? " (clawback)" : "")}"), ct);
        return Unit.Value;
    }
}

public sealed record ListDisputesQuery(Guid TenantId) : IQuery<IReadOnlyList<DisputeDto>>;

public sealed class ListDisputesQueryHandler(ICommissionAdminRepository admin)
    : IQueryHandler<ListDisputesQuery, IReadOnlyList<DisputeDto>>
{
    public Task<IReadOnlyList<DisputeDto>> Handle(ListDisputesQuery q, CancellationToken ct) => admin.ListDisputesAsync(q.TenantId, ct);
}

// ---- Campaigns -----------------------------------------------------------------------------------

public sealed record CreateCampaignCommand(Guid TenantId, CreateCampaignRequest Request) : ICommand<Guid>;

public sealed class CreateCampaignValidator : AbstractValidator<CreateCampaignCommand>
{
    public CreateCampaignValidator()
    {
        RuleFor(x => x.Request.CampaignName).NotEmpty();
        RuleFor(x => x.Request.BonusType).Must(t => t is "flat_bonus_per_booking" or "percentage_multiplier" or "tier_upgrade");
        RuleFor(x => x.Request.EndsAt).GreaterThan(x => x.Request.StartsAt);
    }
}

public sealed class CreateCampaignCommandHandler(
    ICommissionAdminRepository admin, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<CreateCampaignCommand, Guid>
{
    public async Task<Guid> Handle(CreateCampaignCommand command, CancellationToken ct)
    {
        var id = await admin.CreateCampaignAsync(command.TenantId, command.Request, ctx.UserId, clock.UtcNow, ct);
        await audit.RecordAsync(new AuditEntry(
            "create_campaign", "broker_campaign", id, command.Request.CampaignName, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: "Care Partner campaign created"), ct);
        return id;
    }
}

public sealed record ListCampaignsQuery(Guid TenantId) : IQuery<IReadOnlyList<CampaignDto>>;

public sealed class ListCampaignsQueryHandler(ICommissionAdminRepository admin)
    : IQueryHandler<ListCampaignsQuery, IReadOnlyList<CampaignDto>>
{
    public Task<IReadOnlyList<CampaignDto>> Handle(ListCampaignsQuery q, CancellationToken ct) => admin.ListCampaignsAsync(q.TenantId, ct);
}

// ---- Referral links (broker self-service) --------------------------------------------------------

public sealed record CreateReferralLinkCommand(Guid BrokerId, CreateReferralLinkRequest Request) : ICommand<ReferralLinkDto>;

public sealed class CreateReferralLinkCommandHandler(
    IReferralLinkRepository links, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<CreateReferralLinkCommand, ReferralLinkDto>
{
    public async Task<ReferralLinkDto> Handle(CreateReferralLinkCommand command, CancellationToken ct)
    {
        var link = await links.CreateAsync(command.BrokerId, command.Request, clock.UtcNow, ct);
        await audit.RecordAsync(new AuditEntry(
            "create_link", "referral_link", link.LinkId, link.ShortCode, ctx.UserId, command.Request.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: $"Referral link {link.ShortCode}"), ct);
        return link;
    }
}

public sealed record ListReferralLinksQuery(Guid BrokerId) : IQuery<IReadOnlyList<ReferralLinkDto>>;

public sealed class ListReferralLinksQueryHandler(IReferralLinkRepository links)
    : IQueryHandler<ListReferralLinksQuery, IReadOnlyList<ReferralLinkDto>>
{
    public Task<IReadOnlyList<ReferralLinkDto>> Handle(ListReferralLinksQuery q, CancellationToken ct) => links.ListByBrokerAsync(q.BrokerId, ct);
}
