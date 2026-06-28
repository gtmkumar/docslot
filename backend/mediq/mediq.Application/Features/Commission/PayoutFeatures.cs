using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Domain.Commission;
using mediq.SharedDataModel.Docslot.Commission;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Commission;

/// <summary>
/// Creates a payout batch for one broker over a settlement window: aggregates ready-to-pay attributions →
/// gross → TDS (194H) → GST → net, enforcing the ₹100 minimum. Created in 'pending' (NOT yet approved or
/// executed). Gated by <c>commission.payouts.approve</c>? No — creation is part of the read/approve surface;
/// the controller gates batch creation on <c>commission.payouts.read</c>+approve. APPROVAL and EXECUTION are
/// SEPARATE commands gated by DISTINCT permission keys (see controller).
/// </summary>
public sealed record CreatePayoutBatchCommand(Guid TenantId, CreatePayoutBatchRequest Request) : ICommand<PayoutDto>;

public sealed class CreatePayoutBatchValidator : AbstractValidator<CreatePayoutBatchCommand>
{
    public CreatePayoutBatchValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request.BrokerId).NotEmpty();
        RuleFor(x => x.Request.PeriodEnd).GreaterThanOrEqualTo(x => x.Request.PeriodStart);
    }
}

public sealed class CreatePayoutBatchCommandHandler(
    IPayoutRepository payouts, IAttributionRepository attributions, IBrokerRepository brokers,
    IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<CreatePayoutBatchCommand, PayoutDto>
{
    public async Task<PayoutDto> Handle(CreatePayoutBatchCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var now = clock.UtcNow;

        var broker = await brokers.GetByIdAsync(req.BrokerId, ct) ?? throw new KeyNotFoundException("Broker not found.");
        if (broker.IsBlacklisted)
            throw new ForbiddenException("Broker is blacklisted; payout not allowed.");

        var attributionIds = await attributions.ReadyToPayAttributionIdsAsync(command.TenantId, req.BrokerId, ct);
        var gross = await attributions.ReadyToPayGrossAsync(command.TenantId, req.BrokerId, ct);

        var gstRegistered = await brokers.GstRegisteredAsync(req.BrokerId, ct);
        var breakdown = PayoutCalculator.Compute(gross, gstRegistered);
        if (!breakdown.MeetsMinimum)
            throw new BusinessRuleException($"Net payout ₹{breakdown.NetInr} is below the ₹{PayoutCalculator.MinimumPayoutInr} minimum.");

        var payout = Payout.CreatePending(
            command.TenantId, req.BrokerId, req.PeriodStart, req.PeriodEnd, attributionIds.Count,
            breakdown, broker.PayoutMethod, now);
        var payoutId = await payouts.CreateAsync(payout, ct);

        await audit.RecordAsync(new AuditEntry(
            "create_payout", "payout", payoutId, null, ctx.UserId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Payout batch: gross ₹{gross}, TDS ₹{breakdown.TdsInr}, GST ₹{breakdown.GstInr}, net ₹{breakdown.NetInr}"), ct);

        return Map(payoutId, payout, broker.FullName, breakdown, "pending", null);
    }

    internal static PayoutDto Map(Guid id, Payout p, string brokerName, PayoutBreakdown b, string status, string? reference) =>
        new(id, p.BrokerId, brokerName, p.PeriodStart, p.PeriodEnd, p.AttributionCount, b.GrossInr, b.TdsRate, b.TdsInr,
            b.GstRate, b.GstInr, b.NetInr, status, reference);
}

// ---- Approve (STEP 1 — distinct permission: commission.payouts.approve) --------------------------

public sealed record ApprovePayoutCommand(Guid TenantId, Guid PayoutId) : ICommand<PayoutActionResult>;

public sealed class ApprovePayoutCommandHandler(
    IPayoutRepository payouts, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<ApprovePayoutCommand, PayoutActionResult>
{
    public async Task<PayoutActionResult> Handle(ApprovePayoutCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var payout = await payouts.GetByIdAsync(command.PayoutId, command.TenantId, ct)
            ?? throw new KeyNotFoundException("Payout not found.");
        if (payout.Status != "pending")
            throw new BusinessRuleException($"Only a pending payout can be approved (current: {payout.Status}).");

        await payouts.ApproveAsync(payout.PayoutId, userId, clock.UtcNow, ct);
        await audit.RecordAsync(new AuditEntry(
            "approve_payout", "payout", payout.PayoutId, null, userId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: "Payout approved (awaiting execution)"), ct);

        return new PayoutActionResult(payout.PayoutId, "approved", null);
    }
}

// ---- Execute (STEP 2 — distinct permission: commission.payouts.execute) --------------------------

/// <summary>
/// Triggers the actual disbursement through <see cref="IPayoutGateway"/>. REQUIRES the payout to be 'approved'
/// (approval≠execution: the two steps are gated by DISTINCT permission keys at the API). The gateway is the
/// dev <c>StubPayoutGateway</c> (honest DRY RUN — a labelled <c>DRYRUN-…</c> reference, never a fabricated UTR)
/// until real credentials wire a live adapter. On gateway success the batch + its attributions go 'paid' and
/// the wallet moves ready_to_pay→lifetime_paid; on failure the batch goes 'failed' and NO money/wallet moves.
/// </summary>
public sealed record ExecutePayoutCommand(Guid TenantId, Guid PayoutId) : ICommand<PayoutActionResult>;

public sealed class ExecutePayoutCommandHandler(
    IPayoutRepository payouts, IAttributionRepository attributions, IBrokerWalletRepository wallets,
    IPayoutGateway gateway, IBrokerEventPublisher events, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<ExecutePayoutCommand, PayoutActionResult>
{
    public async Task<PayoutActionResult> Handle(ExecutePayoutCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var payout = await payouts.GetByIdAsync(command.PayoutId, command.TenantId, ct)
            ?? throw new KeyNotFoundException("Payout not found.");

        // Approval is a precondition for execution.
        if (!payout.CanExecute)
            throw new BusinessRuleException($"Payout must be approved before execution (current: {payout.Status}).");

        // Atomically claim the payout (approved → processing) BEFORE touching the gateway. A concurrent second
        // execute loses the claim and is rejected here — so the gateway is called once and the wallet credited
        // once (auditor P2 Finding 1: execute must be concurrency-safe / not double-disburse).
        if (!await payouts.TryClaimForExecutionAsync(payout.PayoutId, command.TenantId, clock.UtcNow, ct))
            throw new BusinessRuleException("Payout is not approved or is already being executed.");

        // Disburse via the configured gateway (dev = honest dry-run; never a fake "paid").
        var result = await gateway.SendAsync(
            new PayoutInstruction(payout.PayoutId, payout.BrokerId, payout.NetAmountInr, payout.PaymentMethod, UpiId: null), ct);

        if (!result.Success)
        {
            await payouts.MarkFailedAsync(payout.PayoutId, clock.UtcNow, ct);
            await audit.RecordAsync(new AuditEntry(
                "execute_payout", "payout", payout.PayoutId, null, userId, command.TenantId,
                ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: false,
                ChangeSummary: $"Payout FAILED via {result.GatewayName}: {result.Error}"), ct);
            return new PayoutActionResult(payout.PayoutId, "failed", null);
        }

        await payouts.MarkPaidAsync(payout.PayoutId, result.Reference, result.GatewayName, clock.UtcNow, ct);

        // Mark the batch's attributions paid + apply the broker wallet transition (gross moves to lifetime_paid).
        var attributionIds = await attributions.ReadyToPayAttributionIdsAsync(command.TenantId, payout.BrokerId, ct);
        await attributions.MarkPaidAsync(attributionIds, payout.PayoutId, clock.UtcNow, ct);
        await wallets.ApplyPaidAsync(payout.BrokerId, payout.GrossAmountInr, clock.UtcNow, ct);

        await audit.RecordAsync(new AuditEntry(
            "execute_payout", "payout", payout.PayoutId, result.Reference, userId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Payout executed via {result.GatewayName}{(result.IsDryRun ? " (DRY RUN)" : "")}: net ₹{payout.NetAmountInr} → {result.Reference}"), ct);

        // Integration event — IDs + amount ONLY, no PHI.
        await events.PublishAsync("commission.payout.paid", command.TenantId,
            new { payout_id = payout.PayoutId, broker_id = payout.BrokerId, net_inr = payout.NetAmountInr, reference = result.Reference, dry_run = result.IsDryRun }, ct);

        return new PayoutActionResult(payout.PayoutId, "paid", result.Reference);
    }
}

// ---- List payouts (read) -------------------------------------------------------------------------

public sealed record ListPayoutsQuery(Guid TenantId, int Skip, int Take) : IQuery<IReadOnlyList<PayoutDto>>;

public sealed class ListPayoutsQueryHandler(IPayoutRepository payouts)
    : IQueryHandler<ListPayoutsQuery, IReadOnlyList<PayoutDto>>
{
    public Task<IReadOnlyList<PayoutDto>> Handle(ListPayoutsQuery q, CancellationToken ct)
        => payouts.ListByTenantAsync(q.TenantId, q.Skip, Math.Clamp(q.Take, 1, 200), ct);
}

// ---- List attributions (read) — attribution ledger, masked patient identity ----------------------

public sealed record ListAttributionsQuery(Guid TenantId, int Skip, int Take) : IQuery<IReadOnlyList<AttributionListItemDto>>;

public sealed class ListAttributionsQueryHandler(IAttributionRepository attributions)
    : IQueryHandler<ListAttributionsQuery, IReadOnlyList<AttributionListItemDto>>
{
    public Task<IReadOnlyList<AttributionListItemDto>> Handle(ListAttributionsQuery q, CancellationToken ct)
        => attributions.ListByTenantAsync(q.TenantId, q.Skip, Math.Clamp(q.Take, 1, 200), ct);
}
