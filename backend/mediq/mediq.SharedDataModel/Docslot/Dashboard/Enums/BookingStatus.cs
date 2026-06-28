using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using mediq.SharedDataModel.Json;

namespace mediq.SharedDataModel.Docslot.Dashboard.Enums;

/// <summary>
/// Lifecycle status of a booking.
/// <para>
/// Mirrors the CHECK constraint on <c>docslot.bookings.status</c>
/// (database/03_docslot.sql, table D10):
/// <c>CHECK (status IN ('pending', 'confirmed', 'cancelled', 'completed', 'no_show', 'rescheduled'))</c>.
/// </para>
/// <para>
/// The wire value (<see cref="EnumMemberAttribute.Value"/>) is the EXACT snake_case
/// string stored in PostgreSQL. Serialize/deserialize against these strings — never
/// the C# member name or the numeric value — so the contract round-trips the canonical
/// DB token. The orchestrator brief listed 5 values; the canonical schema has 6
/// (<c>rescheduled</c> is included). SQL wins (ADR-007).
/// </para>
/// </summary>
[DataContract]
[JsonConverter(typeof(EnumMemberJsonConverter))]
public enum BookingStatus
{
    /// <summary>Awaiting reception-desk approval. Drives the "live queue" stat card.</summary>
    [EnumMember(Value = "pending")]
    Pending,

    /// <summary>Approved/confirmed for the slot.</summary>
    [EnumMember(Value = "confirmed")]
    Confirmed,

    /// <summary>Patient has arrived at the front desk (confirmed → checked_in).</summary>
    [EnumMember(Value = "checked_in")]
    CheckedIn,

    /// <summary>Cancelled (carries <c>cancellation_reason</c> / <c>cancelled_at</c>).</summary>
    [EnumMember(Value = "cancelled")]
    Cancelled,

    /// <summary>Consultation completed.</summary>
    [EnumMember(Value = "completed")]
    Completed,

    /// <summary>Patient did not show. Feeds the no-show-rate stat card.</summary>
    [EnumMember(Value = "no_show")]
    NoShow,

    /// <summary>Moved to a different slot (superseded by a new booking row).</summary>
    [EnumMember(Value = "rescheduled")]
    Rescheduled
}
