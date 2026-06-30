namespace mediq.Application.Abstractions;

/// <summary>
/// Persistence seam for the proactive no-show backfill (slice 16). Both methods invoke the schema's two
/// SECURITY DEFINER functions (database/03_docslot.sql) so a context-less BACKGROUND worker can reach the
/// RLS-protected <c>docslot.bookings</c> without a tenant/request GUC (the rls-cross-tenant-worker pattern):
/// the definer functions own the cross-tenant read + the idempotency mark. The functions expose ONLY non-PHI
/// booking metadata (ids + lead-time / slot-hour / on-behalf features) — never patient identity — so this is a
/// plain app-role call: NO <c>BeginTenantScopeAsync</c> / GUC set is needed.
/// </summary>
public interface INoShowBackfillStore
{
    /// <summary>Lists upcoming pending/confirmed bookings whose slot falls within <paramref name="windowHours"/>
    /// and which have not yet been scored (<c>no_show_predicted_at IS NULL</c>), capped at <paramref name="limit"/>.
    /// Cross-tenant by design (the worker has no tenant context); NON-PHI features only.</summary>
    Task<IReadOnlyList<DueNoShowBooking>> ListDueAsync(int windowHours, int limit, CancellationToken ct);

    /// <summary>Marks a booking as scored (<c>no_show_predicted_at = NOW()</c>) so the scan never re-predicts it.
    /// This is the idempotency key for the otherwise-stateless backfill.</summary>
    Task MarkPredictedAsync(Guid bookingId, CancellationToken ct);
}

/// <summary>A due, not-yet-scored booking projected from <c>docslot.list_due_noshow_bookings</c>. Carries the
/// booking + tenant ids and the THREE non-PHI features the AI no-show model consumes (<see cref="NoShowFeatures"/>).
/// No patient identity / PHI is ever surfaced here.</summary>
public sealed record DueNoShowBooking(Guid BookingId, Guid TenantId, int LeadTimeDays, int SlotHour, bool IsBehalf);

/// <summary>
/// Orchestrates a single backfill pass: list due bookings → for each, mint a short-lived per-tenant service
/// token, ask the AI sibling to score it, and (only on a non-null risk) mark it scored. A null risk (AI
/// unavailable) or a per-booking exception leaves the booking unmarked so the next tick retries it. The
/// background worker (<c>NoShowPredictionWorker</c>) is a thin timer that calls this once per tick; the runner
/// holds all the logic so it is unit/integration-testable WITHOUT the timer.
/// </summary>
public interface INoShowBackfillRunner
{
    /// <summary>Runs one backfill pass and returns the number of bookings successfully scored + marked.</summary>
    Task<int> RunOnceAsync(CancellationToken ct);
}
