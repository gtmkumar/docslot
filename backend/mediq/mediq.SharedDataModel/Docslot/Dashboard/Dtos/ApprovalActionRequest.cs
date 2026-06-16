namespace mediq.SharedDataModel.Docslot.Dashboard.Dtos;

/// <summary>
/// The action verbs the reception desk can apply to a booking from the approval queue.
/// These map to <c>docslot.bookings.status</c> transitions and are recorded in
/// <c>docslot.booking_status_history</c> (auto-logged by trigger
/// <c>trg_booking_status_log</c>).
/// </summary>
public enum BookingActionType
{
    /// <summary>pending → confirmed. Sets <c>confirmed_at</c>.</summary>
    Approve,

    /// <summary>* → cancelled. Sets <c>cancelled_at</c>, <c>cancellation_reason</c>, <c>cancelled_by_user_id</c>. Requires a reason.</summary>
    Cancel,

    /// <summary>* → no_show. Sets <c>no_show_at</c>. Feeds the no-show-rate card.</summary>
    MarkNoShow,

    /// <summary>confirmed → completed. Sets <c>completed_at</c>.</summary>
    Complete
}

/// <summary>
/// Contract for an approval-queue action against a single booking (approve / cancel /
/// mark-no-show / complete). This is a BOOKING MUTATION, so it is idempotent
/// (<see cref="IIdempotentRequest"/>) and must carry an <c>Idempotency-Key</c>.
/// <para>
/// Wave 2 is contract-only: no endpoint/handler exists yet. When endpoints land this
/// becomes the body of an <c>ICommand</c> dispatched through the custom CQRS pipeline;
/// the validation behavior asserts <see cref="IdempotencyKey"/> presence and that
/// <see cref="Reason"/> is non-empty when <see cref="Action"/> is <c>Cancel</c>.
/// </para>
/// </summary>
/// <param name="BookingId">Target booking. Maps to <c>bookings.booking_id</c>.</param>
/// <param name="Action">Which transition to apply (see <see cref="BookingActionType"/>).</param>
/// <param name="Reason">
/// Required for <c>Cancel</c> (→ <c>bookings.cancellation_reason</c>); optional otherwise.
/// </param>
/// <param name="IdempotencyKey">Mirrors the <c>Idempotency-Key</c> header. See <see cref="IIdempotentRequest"/>.</param>
public sealed record ApprovalActionRequest(
    Guid BookingId,
    BookingActionType Action,
    string? Reason,
    string? IdempotencyKey) : IIdempotentRequest;
