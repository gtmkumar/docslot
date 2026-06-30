using mediq.Application.Abstractions;
using mediq.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace mediq.Application.Features.Docslot.NoShow;

/// <summary>
/// The orchestration heart of the proactive no-show backfill (slice 16). One <see cref="RunOnceAsync"/> pass:
/// <list type="number">
/// <item>lists the due (upcoming, not-yet-scored) bookings via the cross-tenant SECURITY DEFINER scan;</item>
/// <item>for EACH booking, in an isolating per-booking try/catch: mints a SHORT-LIVED, PER-TENANT SERVICE token
/// (<c>token_use=service</c>, a fixed non-human subject) and asks the AI sibling to score the booking, passing
/// that token as the explicit bearer (the worker has no live caller / HttpContext);</item>
/// <item>on a non-null risk it marks the booking scored (the idempotency marker), so the next scan excludes it.</item>
/// </list>
/// Resilience &amp; honesty: a NULL risk (AI unavailable / errored) or a thrown exception leaves the booking
/// UNMARKED, so the next tick retries it — the worker never fabricates a score and never marks on failure. One
/// booking's failure never aborts the batch. SECURITY: the service token is NEVER logged (only counts/status);
/// the features are non-PHI and the booking id is the only identifier touched.
/// </summary>
public sealed class NoShowBackfillRunner(
    INoShowBackfillStore store,
    IAiNoShowClient ai,
    ITokenService tokens,
    IOptions<NoShowBackfillOptions> opts,
    ILogger<NoShowBackfillRunner> logger) : INoShowBackfillRunner
{
    private readonly NoShowBackfillOptions _o = opts.Value;

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var due = await store.ListDueAsync(_o.WindowHours, _o.BatchSize, ct);
        if (due.Count == 0)
            return 0;

        logger.LogDebug("NoShowBackfill: {Count} due booking(s) to score (window={WindowHours}h).", due.Count, _o.WindowHours);

        var predicted = 0;
        foreach (var b in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // A fresh, short-TTL service token PER tenant — the worker has no human caller, so it presents a
                // token_use=service token the AI accepts only on the non-PHI scoring path. NEVER logged.
                var token = tokens.CreateServiceToken(b.TenantId, _o.ServiceSubject, _o.ServiceTokenTtlMinutes);

                var risk = await ai.PredictAsync(
                    b.BookingId,
                    new NoShowFeatures(b.LeadTimeDays, b.SlotHour, b.IsBehalf),
                    serviceBearer: token.Value,
                    ct);

                if (risk is not null)
                {
                    // Mark only on a real score — a null (AI unavailable) leaves it due for the next tick.
                    await store.MarkPredictedAsync(b.BookingId, ct);
                    predicted++;
                    logger.LogDebug("NoShowBackfill: scored booking {BookingId} (band={Band}).", b.BookingId, risk.Band);
                }
                else
                {
                    // Honest unavailability — do NOT mark; the next tick retries.
                    logger.LogInformation("NoShowBackfill: booking {BookingId} risk unavailable; will retry next tick.", b.BookingId);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;   // shutdown — propagate so the worker exits cleanly
            }
            catch (Exception ex)
            {
                // One booking's failure must not abort the batch; leave it unmarked so it retries.
                logger.LogWarning(ex, "NoShowBackfill: scoring booking {BookingId} failed; left unmarked for retry.", b.BookingId);
            }
        }

        logger.LogInformation("NoShowBackfill: scored {Predicted}/{Due} due booking(s).", predicted, due.Count);
        return predicted;
    }
}
