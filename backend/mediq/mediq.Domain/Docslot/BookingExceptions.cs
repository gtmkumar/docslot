namespace mediq.Domain.Docslot;

/// <summary>
/// Raised when a booking state transition is not permitted by the lifecycle state machine
/// (e.g. completing a pending booking, or acting on an already-terminal booking). The Application layer
/// maps this to a 422 (business-rule violation) via the shared exception handler.
/// </summary>
public sealed class InvalidBookingTransitionException(BookingStatus from, string action)
    : Exception($"Cannot '{action}' a booking in status '{from.ToToken()}'.")
{
    public BookingStatus From { get; } = from;
    public string Action { get; } = action;
}

/// <summary>Raised when a slot cannot be held (already booked/blocked, or hold capacity reached).</summary>
public sealed class SlotUnavailableException(Guid slotId)
    : Exception($"Slot '{slotId}' is not available to hold.");
