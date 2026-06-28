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
/// until real credentials wire a live adapter.
/// <para>
/// <b>Two-phase, gateway-outside-the-transaction (auditor gateway-go-live F2 + the ExecutePayout-idempotency
/// HIGH).</b> The disbursement is an EXTERNAL side effect, so it must not run inside a DB transaction:
/// </para>
/// <list type="number">
///   <item><b>Phase 1 (own committed tx):</b> atomically claim approved → 'processing' and COMMIT, so the intent
///   is DURABLE and no payout row lock is held across the network call. Resolves replays (already 'paid' →
///   return the recorded reference, no second gateway call) and crash-recovery (already 'processing' → resume).</item>
///   <item><b>Phase 2 (NO tx):</b> call the gateway with the payout id as the idempotency key. A retry after a
///   mid-flight crash re-sends the SAME key, so a correct gateway returns the original transfer — the money moves
///   at most once. An ambiguous failure (exception) leaves the payout 'processing' for a later resume; it is NOT
///   marked failed (that would release it for a fresh disbursement and risk a double-pay).</item>
///   <item><b>Phase 3 (own committed tx):</b> SINGLE-WINNER finalize — only the caller whose conditional
///   processing→paid (or →failed) UPDATE matched a row applies the wallet + attribution side effects, so even a
///   concurrent resume credits exactly once.</item>
/// </list>
/// Marked <see cref="ISelfManagedTransaction"/> so the UnitOfWork behavior does NOT wrap it in one ambient tx.
/// </summary>
public sealed record ExecutePayoutCommand(Guid TenantId, Guid PayoutId) : ICommand<PayoutActionResult>, ISelfManagedTransaction;

public sealed class ExecutePayoutCommandHandler(
    IPayoutRepository payouts, IAttributionRepository attributions, IBrokerWalletRepository wallets,
    IPayoutGateway gateway, IBrokerEventPublisher events, IAuditTrailWriter audit, ICurrentUserContext ctx,
    IClock clock, IUnitOfWork uow)
    : ICommandHandler<ExecutePayoutCommand, PayoutActionResult>
{
    public async Task<PayoutActionResult> Handle(ExecutePayoutCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");

        // ── Phase 1 — durably claim approved → 'processing' (or short-circuit on a replay/resume) in its OWN
        //    committed transaction, releasing the row lock before the external gateway call below.
        Payout payout;
        await using (var claim = await uow.BeginTenantScopeAsync(command.TenantId, ct))
        {
            payout = await payouts.GetByIdAsync(command.PayoutId, command.TenantId, ct)
                ?? throw new KeyNotFoundException("Payout not found.");

            switch (payout.Status)
            {
                case "paid":
                    // Idempotent replay — the disbursement already completed. Return the recorded reference; do
                    // NOT call the gateway again, do NOT re-credit the wallet.
                    await claim.CommitAsync(ct);
                    return new PayoutActionResult(payout.PayoutId, "paid", payout.PaymentReference);

                case "processing":
                    // Crash-recovery RESUME: a prior execution already claimed it and may have already reached the
                    // gateway. Re-call the gateway below with the same idempotency key (dedupes → at most one
                    // disbursement). No re-claim — it is no longer 'approved'.
                    await claim.CommitAsync(ct);
                    break;

                case "approved":
                    // Atomic single-winner claim: a concurrent second execute matches 0 rows here and is rejected.
                    if (!await payouts.TryClaimForExecutionAsync(payout.PayoutId, command.TenantId, clock.UtcNow, ct))
                        throw new BusinessRuleException("Payout is already being executed.");
                    await claim.CommitAsync(ct);   // 'processing' is now durable; lock released
                    break;

                default:
                    throw new BusinessRuleException($"Payout must be approved before execution (current: {payout.Status}).");
            }
        }

        // ── Phase 2 — disburse via the gateway OUTSIDE any DB transaction (no lock held across network I/O). The
        //    payout id is the idempotency key so a resumed retry never double-disburses. Dev = honest dry-run.
        PayoutGatewayResult result;
        try
        {
            result = await gateway.SendAsync(
                new PayoutInstruction(payout.PayoutId, payout.BrokerId, payout.NetAmountInr, payout.PaymentMethod,
                    UpiId: null, IdempotencyKey: payout.PayoutId.ToString()), ct);
        }
        catch (Exception ex)
        {
            // Ambiguous outcome (network/timeout): the transfer MAY have happened. Leave the payout 'processing'
            // (a later execute resumes via the idempotency key) — do NOT mark failed. Audit the ambiguous attempt.
            await using var amb = await uow.BeginTenantScopeAsync(command.TenantId, ct);
            await audit.RecordAsync(new AuditEntry(
                "execute_payout", "payout", payout.PayoutId, null, userId, command.TenantId,
                ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: false,
                ChangeSummary: $"Payout gateway call did not return a definite result (left 'processing' for retry): {ex.Message}"), ct);
            await amb.CommitAsync(ct);
            throw;
        }

        // ── Phase 3 — record the outcome in a fresh committed transaction; the conditional UPDATE is the
        //    single-winner gate that guards the wallet/attribution side effects against a concurrent resume.
        await using (var settle = await uow.BeginTenantScopeAsync(command.TenantId, ct))
        {
            if (!result.Success)
            {
                if (await payouts.MarkFailedAsync(payout.PayoutId, clock.UtcNow, ct))
                    await audit.RecordAsync(new AuditEntry(
                        "execute_payout", "payout", payout.PayoutId, null, userId, command.TenantId,
                        ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: false,
                        ChangeSummary: $"Payout FAILED via {result.GatewayName}: {result.Error}"), ct);
                await settle.CommitAsync(ct);
                return new PayoutActionResult(payout.PayoutId, "failed", null);
            }

            // Only the finalize winner applies the money movement (processing → paid matched a row).
            if (await payouts.MarkPaidAsync(payout.PayoutId, result.Reference, result.GatewayName, clock.UtcNow, ct))
            {
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
            }

            await settle.CommitAsync(ct);
        }

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
