using mediq.Application.Abstractions;

namespace mediq.Application.Features.Commission;

/// <summary>
/// Wires the commission EARNING lifecycle to booking lifecycle events — the missing link that left every
/// attribution stuck at 'pending' (so payout batches were always empty). Runs inside the booking action
/// handler's tenant-scoped UoW, so the attribution status change + wallet move + event commit atomically with
/// the booking transition.
/// </summary>
public sealed class CommissionLifecycleService(
    IAttributionRepository attributions,
    IBrokerWalletRepository wallets,
    IBrokerEventPublisher events)
    : ICommissionLifecycleService
{
    public async Task OnBookingCompletedAsync(Guid tenantId, Guid bookingId, DateTime now, CancellationToken ct)
    {
        var earned = await attributions.MarkEarnedForBookingAsync(tenantId, bookingId, now, ct);
        foreach (var e in earned)
        {
            if (e.Amount > 0m)
                await wallets.ApplyEarnedAsync(e.BrokerId, e.Amount, now, ct);
            // Integration event — IDs + amount ONLY, never patient PHI.
            await events.PublishAsync("commission.commission.earned", tenantId,
                new { booking_id = bookingId, broker_id = e.BrokerId, commission_inr = e.Amount }, ct);
        }
    }

    public async Task OnBookingReversedAsync(Guid tenantId, Guid bookingId, DateTime now, CancellationToken ct)
    {
        // A cancelled / no-show booking earns no commission: reverse any not-yet-paid attributions and
        // debit the wallet bucket each was sitting in.
        var reversed = await attributions.MarkReversedForBookingAsync(tenantId, bookingId, now, ct);
        foreach (var r in reversed)
        {
            if (r.Amount > 0m)
                await wallets.ApplyReversedAsync(r.BrokerId, r.Amount, r.FromStatus, now, ct);
            await events.PublishAsync("commission.commission.reversed", tenantId,
                new { booking_id = bookingId, broker_id = r.BrokerId, commission_inr = r.Amount, from_status = r.FromStatus }, ct);
        }
    }
}
