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
    /// Atomically places a TTL hold on an available slot. Returns the hold token on success; throws
    /// <see cref="mediq.Domain.Docslot.SlotUnavailableException"/> if the slot is booked/blocked/at capacity.
    /// </summary>
    Task<SlotHold> HoldAsync(Guid tenantId, Guid slotId, TimeSpan ttl, DateTime nowUtc, CancellationToken ct);

    /// <summary>Confirms (consumes) a hold when a booking is created against it; increments slot count.</summary>
    Task ConvertAsync(Guid holdId, Guid bookingId, DateTime nowUtc, CancellationToken ct);

    /// <summary>Releases a hold (booking abandoned/cancelled before confirm), freeing slot capacity.</summary>
    Task ReleaseAsync(Guid holdId, DateTime nowUtc, CancellationToken ct);

    /// <summary>True if the hold exists and has not expired.</summary>
    Task<bool> IsLiveAsync(Guid holdId, DateTime nowUtc, CancellationToken ct);
}

public sealed record SlotHold(Guid HoldId, Guid SlotId, string HoldToken, DateTime ExpiresAtUtc);

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
