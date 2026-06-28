namespace mediq.Application.Abstractions;

/// <summary>
/// Slot hold-on-selection with a 5-minute TTL (FR-BOOK-02). Holding atomically reserves capacity on a
/// slot; the hold expires automatically so abandoned selections free up. Confirmed bookings convert the
/// hold. NOTE: the canonical schema has no slot-hold table — this is backed by an APP-OWNED operational
/// table (<c>docslot.slot_holds</c>) created at startup; flagged as a candidate canonical addition.
/// </summary>
public interface ISlotHoldService
{
    /// <summary>
    /// Atomically places a TTL hold on an available slot that belongs to <paramref name="doctorId"/>.
    /// Returns the hold token on success; throws <see cref="mediq.Domain.Docslot.SlotUnavailableException"/>
    /// if the slot is booked/blocked/at capacity OR does not belong to that doctor (consistency guard).
    /// </summary>
    Task<SlotHold> HoldAsync(Guid tenantId, Guid slotId, Guid doctorId, TimeSpan ttl, DateTime nowUtc, CancellationToken ct);

    /// <summary>Confirms (consumes) a hold when a booking is created against it; increments slot count.</summary>
    Task ConvertAsync(Guid holdId, Guid bookingId, DateTime nowUtc, CancellationToken ct);

    /// <summary>Releases a hold (booking abandoned/cancelled before confirm), freeing slot capacity.</summary>
    Task ReleaseAsync(Guid holdId, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// Frees the capacity a CONFIRMED/converted booking consumed when that booking is cancelled or marked
    /// no-show: decrements <c>current_count</c> (floored at 0) and re-opens the slot to 'available' if it
    /// had been marked 'booked'. Idempotent enough for retries (won't go below zero).
    /// </summary>
    Task ReleaseSlotCapacityAsync(Guid slotId, DateTime nowUtc, CancellationToken ct);

    /// <summary>Sweeps stale live holds (expires_at &lt; now) to 'expired'. Returns rows swept.</summary>
    Task<int> ExpireStaleHoldsAsync(DateTime nowUtc, CancellationToken ct);

    /// <summary>True if the hold exists and has not expired.</summary>
    Task<bool> IsLiveAsync(Guid holdId, DateTime nowUtc, CancellationToken ct);
}

public sealed record SlotHold(Guid HoldId, Guid SlotId, string HoldToken, DateTime ExpiresAtUtc);

/// <summary>
/// Materializes bookable <c>docslot.time_slots</c> from a doctor's recurring weekly <c>doctor_schedules</c>
/// (honouring <c>schedule_overrides</c> + breaks) via the <c>docslot.generate_time_slots</c> SECURITY DEFINER
/// function. Used by the staff "generate" endpoint and the nightly materializer worker.
/// </summary>
public interface ISlotGenerationService
{
    /// <summary>Generate slots for one doctor over [from,to] (inclusive). Returns rows created. Idempotent.</summary>
    Task<int> GenerateAsync(Guid doctorId, DateOnly fromDate, DateOnly toDate, CancellationToken ct);

    /// <summary>Generate a rolling horizon for EVERY active doctor (the nightly materializer). Returns rows created.</summary>
    Task<int> GenerateRollingHorizonAsync(DateOnly fromDate, int horizonDays, CancellationToken ct);
}

/// <summary>
/// Records a purpose-of-use declaration for a patient-record read into <c>platform.purpose_of_use_log</c>
/// (DPDP — every full patient read must declare why). Break-glass is supported but flagged for review.
/// </summary>
public interface IPurposeOfUseWriter
{
    Task RecordAsync(PurposeOfUseEntry entry, CancellationToken ct);
}

public sealed record PurposeOfUseEntry(
    Guid UserId, Guid TenantId, string ResourceType, Guid ResourceId, string DeclaredPurpose,
    string? PurposeNotes, bool IsBreakGlass, string? BreakGlassReason);

/// <summary>
/// Issues an OPD queue token for a confirmed booking (maps to <c>docslot.opd_tokens</c>). The next
/// token_number per (doctor, date) is allocated server-side. Optional — hospitals use it; clinics may not.
/// </summary>
public interface IOpdTokenService
{
    Task<int> IssueAsync(Guid tenantId, Guid bookingId, Guid doctorId, DateOnly date, DateTime nowUtc, CancellationToken ct);
}
