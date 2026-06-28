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

    public async Task OnAttributionConfirmedAsync(Guid tenantId, Guid bookingId, DateTime now, CancellationToken ct)
    {
        // Post-hoc claims are usually filed AFTER the visit, so the booking-completed event already fired before
        // the attribution existed. If the booking is already completed, earn the now-confirmed attribution
        // immediately (MarkEarnedForBookingAsync only moves verification-cleared 'pending' rows, so this earns
        // exactly the just-confirmed one). Otherwise leave it 'pending' — the eventual completion earns it.
        if (await attributions.IsBookingCompletedAsync(bookingId, tenantId, ct))
            await OnBookingCompletedAsync(tenantId, bookingId, now, ct);
    }

    public async Task OnAttributionRejectedAsync(Guid tenantId, Guid attributionId, Guid bookingId, string reason, DateTime now, CancellationToken ct)
    {
        // Patient denied (or in-request rejection): reverse the single attribution and debit the bucket it was
        // sitting in (+ lifetime_reversed) so no phantom pending_inr is left credited. Idempotent — ReverseOneAsync
        // returns null if it was already terminal (e.g. a concurrent sweep got there first).
        var reversed = await attributions.ReverseOneAsync(attributionId, tenantId, now, ct);
        if (reversed is null)
            return;
        if (reversed.Amount > 0m)
            await wallets.ApplyReversedAsync(reversed.BrokerId, reversed.Amount, reversed.FromStatus, now, ct);
        await events.PublishAsync("commission.commission.reversed", tenantId,
            new { booking_id = bookingId, broker_id = reversed.BrokerId, commission_inr = reversed.Amount, from_status = reversed.FromStatus, reason }, ct);
    }
}
