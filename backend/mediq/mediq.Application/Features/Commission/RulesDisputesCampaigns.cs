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
    ICommissionAdminRepository admin, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<ResolveDisputeCommand>
{
    public async Task<Unit> Handle(ResolveDisputeCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        await admin.ResolveDisputeAsync(command.Request.DisputeId, command.Request, userId, clock.UtcNow, ct);
        await audit.RecordAsync(new AuditEntry(
            "resolve_dispute", "attribution_dispute", command.Request.DisputeId, null, userId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: $"Resolved: {command.Request.Status}"), ct);
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
