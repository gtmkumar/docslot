using mediq.Application.Abstractions;

namespace mediq.Api.Workers;

/// <summary>
/// Background maintenance for the booking data plane. On a short tick (default 5 min) it sweeps stale slot
/// holds to 'expired' (hygiene — HoldAsync already ignores expired holds logically); and on a slower cadence
/// (default every 12 h, and once at startup) it materializes a rolling horizon of bookable time_slots for
/// every active doctor from their weekly schedules, so production always has inventory to book against.
/// <para>
/// Singleton hosted service → opens a DI scope per tick for the scoped slot services (+ their DbContext).
/// A tick failure is logged and never crashes the host. Disable via Booking:MaintenanceWorkerEnabled=false.
/// </para>
/// </summary>
public sealed class BookingMaintenanceWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<BookingMaintenanceWorker> logger) : BackgroundService
{
    private DateTime _lastMaterializeUtc = DateTime.MinValue;
    private DateTime _lastNudgeUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = TimeSpan.FromMinutes(Math.Max(1, config.GetValue("Booking:MaintenanceTickMinutes", 5)));
        var materializeEvery = TimeSpan.FromHours(Math.Max(1, config.GetValue("Booking:SlotMaterializeIntervalHours", 12)));
        var horizonDays = Math.Max(1, config.GetValue("Booking:SlotHorizonDays", 14));

        logger.LogInformation(
            "BookingMaintenanceWorker started (tick={Tick}m, materialize={Every}h, horizon={Horizon}d).",
            tick.TotalMinutes, materializeEvery.TotalHours, horizonDays);

        using var timer = new PeriodicTimer(tick);
        do
        {
            try
            {
                await TickAsync(materializeEvery, horizonDays, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "BookingMaintenanceWorker tick failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(TimeSpan materializeEvery, int horizonDays, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var now = DateTime.UtcNow;

        var holds = scope.ServiceProvider.GetRequiredService<ISlotHoldService>();
        var swept = await holds.ExpireStaleHoldsAsync(now, ct);
        if (swept > 0)
            logger.LogInformation("BookingMaintenance: swept {Count} expired slot holds.", swept);

        // Requeue outbox rows stranded in 'processing' (worker died mid-send) so they're not lost.
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxDrainStore>();
        var requeued = await outbox.RequeueStrandedAsync(TimeSpan.FromMinutes(5), ct);
        if (requeued > 0)
            logger.LogWarning("BookingMaintenance: requeued {Count} stranded outbox messages.", requeued);

        // Expire lapsed behalf-booking consent OTPs (cancels the awaiting booking + frees its slot).
        var consent = scope.ServiceProvider.GetRequiredService<IConsentOtpStore>();
        var expired = await consent.ExpireStaleAsync(ct);
        if (expired > 0)
            logger.LogInformation("BookingMaintenance: expired {Count} stale consent OTPs (bookings cancelled).", expired);

        // Lapse post-hoc attribution claims with no patient response within the TTL → verification 'no_response'
        // + reverse the attribution + debit the broker wallet (closes the phantom-pending_inr gap).
        var claims = scope.ServiceProvider.GetRequiredService<IAttributionClaimOtpStore>();
        var lapsedClaims = await claims.ExpireStaleAsync(ct);
        if (lapsedClaims > 0)
            logger.LogInformation("BookingMaintenance: lapsed {Count} unanswered attribution claims (reversed).", lapsedClaims);

        // Commission settlement: earned attributions past the window → ready_to_pay (so a refund within the
        // window can still reverse before the money is locked into a payout batch).
        var settleWindow = TimeSpan.FromHours(Math.Max(0, config.GetValue("Commission:SettlementWindowHours", 24)));
        var attributions = scope.ServiceProvider.GetRequiredService<mediq.Application.Abstractions.IAttributionRepository>();
        var settled = await attributions.SettleEarnedAsync(settleWindow, ct);
        if (settled > 0)
            logger.LogInformation("BookingMaintenance: settled {Count} earned attributions → ready_to_pay.", settled);

        if (now - _lastMaterializeUtc >= materializeEvery)
        {
            var gen = scope.ServiceProvider.GetRequiredService<ISlotGenerationService>();
            var created = await gen.GenerateRollingHorizonAsync(DateOnly.FromDateTime(now), horizonDays, ct);
            _lastMaterializeUtc = now;
            logger.LogInformation("BookingMaintenance: materialized {Count} slots (horizon {Days}d).", created, horizonDays);
        }

        // Hidden-Care-Partner conversion nudge (carrot, not stick) — recompute the behalf-booking funnel + nudge
        // eligible numbers via the outbox, on a slow cadence (default daily) with a per-number cooldown so it
        // never nags. DISABLED BY DEFAULT: a PROACTIVE WhatsApp promotional message requires a Meta-APPROVED
        // TEMPLATE (not free-form text) and a recorded DPDP opt-in + honored STOP/opt-out before the live Meta
        // sender may carry it (auditor F1/F2 — pre-production gates). Enabling this flag is the deliberate act
        // that must be paired with those. The dev StubWhatsAppSender only logs, so dev/test is unaffected.
        var nudgeEnabled = config.GetValue("Commission:PartnerNudgeEnabled", false);
        var nudgeEvery = TimeSpan.FromHours(Math.Max(1, config.GetValue("Commission:PartnerNudgeIntervalHours", 24)));
        if (nudgeEnabled && now - _lastNudgeUtc >= nudgeEvery)
        {
            var nudge = scope.ServiceProvider.GetRequiredService<IPartnerNudgeStore>();
            var minPatients = Math.Max(1, config.GetValue("Commission:PartnerNudgeMinPatients", 3));
            var cooldown = TimeSpan.FromDays(Math.Max(1, config.GetValue("Commission:PartnerNudgeCooldownDays", 30)));
            var nudged = await nudge.RunSweepAsync(minPatients, cooldown, ct);
            _lastNudgeUtc = now;
            if (nudged > 0)
                logger.LogInformation("BookingMaintenance: sent {Count} hidden-Care-Partner conversion nudges.", nudged);
        }
    }
}
