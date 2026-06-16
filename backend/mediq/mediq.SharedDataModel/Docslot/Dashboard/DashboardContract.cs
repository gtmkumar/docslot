namespace mediq.SharedDataModel.Docslot.Dashboard;

/// <summary>
/// Central documentation of the cross-cutting contract conventions for the DocSlot
/// reception-desk dashboard read-models and actions. This type holds no behavior — it is
/// the single place that records the conventions every dashboard DTO in this assembly obeys,
/// so the frontend mock seam and the eventual API agree.
/// </summary>
/// <remarks>
/// <para><b>Response envelope (DRY — reused, not redefined).</b>
/// DTOs in this assembly are the <c>Data</c> payloads only. The API wraps them in the
/// existing shared envelopes from <c>mediq.Utilities</c> — do NOT add a parallel envelope
/// to SharedDataModel:
/// <list type="bullet">
/// <item><see cref="DashboardSummaryDto"/> and <c>BookingActionResultDto</c> →
/// <c>mediq.Utilities.ApiResponse.ResponseUtil.SingleResponse&lt;T&gt;</c>.</item>
/// <item>A page of <c>BookingListItemDto</c> →
/// <c>mediq.Utilities.ApiResponse.ResponseUtil.PaginatedListResponse&lt;T&gt;</c>
/// (use <c>mediq.Utilities.Common.PaginatedList&lt;T&gt;</c> to build it).</item>
/// <item>The menu tree (<c>MenuNodeDto</c>) and <c>PermissionSetDto</c> →
/// <c>SingleResponse&lt;T&gt;</c> (the tree/set is one logical object).</item>
/// <item>Errors flow through <c>mediq.Utilities.ApiResponse.ResponseUtil.Message</c> +
/// <c>ErrorMessageEnum</c> and the existing <c>ExceptionHandler</c> middleware — handlers
/// return <c>mediq.Utilities.Results.Result&lt;T&gt;</c> internally.</item>
/// </list>
/// </para>
///
/// <para><b>Timezone contract — Asia/Kolkata.</b>
/// Every user-facing instant in dashboard DTOs is typed <see cref="System.DateTimeOffset"/>.
/// Slot fields (<c>BookingListItemDto.SlotStart</c>/<c>SlotEnd</c>) are composed from the DB's
/// <c>time_slots.slot_date</c> (DATE) + <c>start_time</c>/<c>end_time</c> (TIME, wall-clock) and
/// emitted with the <b>+05:30</b> IST offset. Audit instants stored as TIMESTAMPTZ
/// (<c>booked_at</c>, <c>confirmed_at</c>, …) are emitted as their true UTC-offset instant.
/// Day-bucket aggregates in <see cref="DashboardSummaryDto"/> ("today", no-show rate) are
/// computed against CURRENT_DATE in Asia/Kolkata, never UTC. The frontend renders all slot
/// times with an explicit Asia/Kolkata zone (per CLAUDE.md/REACT_SKILL.md).
/// </para>
///
/// <para><b>PHI / purpose-of-use.</b>
/// A patient's full phone number is PHI under DPDP. List/queue read-models expose ONLY a
/// masked form (<c>BookingListItemDto.MaskedPhone</c>); the unmasked number is never
/// serialized into a list. Revealing the full number, or any full patient-record read, is a
/// separate endpoint that MUST declare purpose-of-use (per CLAUDE.md). Masking is performed by
/// the API; these contracts only ever carry the masked value.
/// </para>
///
/// <para><b>Idempotency.</b>
/// Booking-mutating requests implement <see cref="Dtos.IIdempotentRequest"/> and carry the
/// <c>Idempotency-Key</c> header value. Replaying the same key returns the original
/// <c>BookingActionResultDto</c> (with <c>WasReplayed = true</c>) instead of re-executing.
/// </para>
///
/// <para><b>Enum wire-values.</b>
/// <c>BookingStatus</c>, <c>BookingSource</c>, and <c>Gender</c> serialize to the EXACT
/// snake_case strings of their backing SQL CHECK constraints (carried on
/// <c>[EnumMember(Value=...)]</c>). The serializer (System.Text.Json with a
/// <c>JsonStringEnumMemberName</c>-aware converter, or a custom converter) MUST round-trip
/// those strings — not C# member names or numeric values — so the frontend mirror matches
/// the canonical DB tokens.
/// </para>
/// </remarks>
public static class DashboardContract
{
    /// <summary>IANA timezone every user-facing dashboard instant is expressed in.</summary>
    public const string TimeZoneId = "Asia/Kolkata";

    /// <summary>UTC offset of <see cref="TimeZoneId"/> (India has no DST). Provided for composing slot instants.</summary>
    public static readonly TimeSpan TimeZoneOffset = TimeSpan.FromHours(5.5);

    /// <summary>ISO-4217 currency for all monetary dashboard values (India-only platform).</summary>
    public const string CurrencyCode = "INR";
}
