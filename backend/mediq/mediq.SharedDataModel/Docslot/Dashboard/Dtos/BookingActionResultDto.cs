using mediq.SharedDataModel.Docslot.Dashboard.Enums;

namespace mediq.SharedDataModel.Docslot.Dashboard.Dtos;

/// <summary>
/// Result payload returned after an <see cref="ApprovalActionRequest"/> succeeds.
/// <para>
/// Wave 2 contract: the API wraps this in the shared envelope
/// <c>mediq.Utilities.ApiResponse.ResponseUtil.SingleResponse&lt;BookingActionResultDto&gt;</c>
/// (see <see cref="DashboardContract"/>) — this DTO does NOT redefine status/message.
/// Returned on both first execution and idempotent replay (same key ⇒ same result).
/// </para>
/// </summary>
/// <param name="BookingId">The booking acted upon. Maps to <c>bookings.booking_id</c>.</param>
/// <param name="NewStatus">Resulting lifecycle status. Maps to the updated <c>bookings.status</c>.</param>
/// <param name="TransitionedAt">
/// When the transition was applied, as a UTC-offset instant. Maps to whichever
/// <c>*_at</c> timestamp the action set (<c>confirmed_at</c>/<c>cancelled_at</c>/
/// <c>no_show_at</c>/<c>completed_at</c>).
/// </param>
/// <param name="WasReplayed">
/// True when this response was served from the idempotency store rather than a fresh
/// mutation (i.e. a retried <c>Idempotency-Key</c>). Lets the UI distinguish a no-op replay.
/// </param>
public sealed record BookingActionResultDto(
    Guid BookingId,
    BookingStatus NewStatus,
    DateTimeOffset TransitionedAt,
    bool WasReplayed);
