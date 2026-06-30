namespace mediq.Application.Options;

/// <summary>
/// Configures the phase-4 PROACTIVE NO-SHOW PREDICTION BACKFILL worker (<c>NoShowPredictionWorker</c>). On a
/// configurable cadence it scans for upcoming, not-yet-scored bookings (slot within the look-ahead window,
/// <c>no_show_predicted_at IS NULL</c>) and asks the AI sibling service to score each, marking the booking once
/// scored so it is never re-predicted (the marker is the idempotency key — the scan is otherwise stateless).
/// <para>
/// DEFAULT-OFF (<see cref="Enabled"/> = false) and force-OFF in the integration suite (TestHostConfig). The
/// scan reaches the RLS-protected bookings through two SECURITY DEFINER functions (the rls-cross-tenant-worker
/// pattern) that expose ONLY non-PHI features (lead time / slot hour / on-behalf) — never patient identity. Each
/// booking is scored under a SHORT-LIVED, PER-TENANT SERVICE token (<c>token_use=service</c>), so the worker
/// needs no human caller and the AI can refuse it on every PHI path. A null risk (AI unavailable) or an
/// exception leaves the booking unmarked, so the next tick retries it.
/// </para>
/// </summary>
public sealed class NoShowBackfillOptions
{
    public const string SectionName = "NoShowBackfill";

    /// <summary>Run the background backfill loop in this instance. DEFAULT FALSE — proactive scoring is opt-in;
    /// the worker is only registered (Program) when this is true, and the integration suite forces it false.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Seconds between backfill ticks (default 300 — a calm cadence; advisory scoring, not a hot path).</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>Max bookings scored per tick — an upper bound on the AI calls (and token mints) any single tick
    /// performs; the next tick continues a deeper backlog. Maps to the <c>p_limit</c> of the due-list function.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Look-ahead window (hours) for "upcoming" bookings — only bookings whose slot falls within the
    /// next <c>WindowHours</c> are scored. Maps to the <c>p_window_hours</c> of the due-list function.</summary>
    public int WindowHours { get; set; } = 72;

    /// <summary>TTL (minutes) of the per-booking service token minted for the AI call. Deliberately short — the
    /// token lives only long enough for one out-of-band call. <c>CreateServiceToken</c> clamps it to [1,15].</summary>
    public int ServiceTokenTtlMinutes { get; set; } = 5;

    /// <summary>The fixed NON-HUMAN service subject stamped into the token's <c>sub</c> claim (never a user id),
    /// so the caller is identifiable as the worker — not a person — in the token and in AI-side logs. NOTE:
    /// no-show scoring is non-PHI/advisory and the AI's <c>audit_log.user_id</c> is a UUID FK to
    /// <c>platform.users</c>, so this string subject writes NO clinical audit row (the prediction row itself
    /// still persists). Pre-prod: seed a dedicated service-account user UUID and mint <c>sub</c> = that id if a
    /// DB audit trail for service scoring is later required.</summary>
    public string ServiceSubject { get; set; } = "svc:no-show-predictor";
}
