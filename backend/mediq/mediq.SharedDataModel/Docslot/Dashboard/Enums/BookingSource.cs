using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using mediq.SharedDataModel.Json;

namespace mediq.SharedDataModel.Docslot.Dashboard.Enums;

/// <summary>
/// Channel a booking originated from.
/// <para>
/// Mirrors the CHECK constraint on <c>docslot.bookings.booked_via</c>
/// (database/03_docslot.sql, table D10):
/// <c>CHECK (booked_via IN ('whatsapp', 'dashboard', 'api', 'walk_in', 'phone_call'))</c>.
/// </para>
/// <para>
/// The orchestrator brief named "whatsapp/walk-in/phone" — the canonical column is
/// <c>booked_via</c> with FIVE values (adds <c>dashboard</c> and <c>api</c>, and the
/// walk-in/phone tokens are <c>walk_in</c>/<c>phone_call</c>). SQL wins (ADR-007).
/// Wire values are the EXACT snake_case DB tokens.
/// </para>
/// </summary>
[DataContract]
[JsonConverter(typeof(EnumMemberJsonConverter))]
public enum BookingSource
{
    /// <summary>Booked through the WhatsApp conversational flow.</summary>
    [EnumMember(Value = "whatsapp")]
    Whatsapp,

    /// <summary>Booked by staff via the reception-desk dashboard.</summary>
    [EnumMember(Value = "dashboard")]
    Dashboard,

    /// <summary>Booked programmatically via the public/partner API.</summary>
    [EnumMember(Value = "api")]
    Api,

    /// <summary>Walk-in registered at the desk.</summary>
    [EnumMember(Value = "walk_in")]
    WalkIn,

    /// <summary>Booked over a phone call.</summary>
    [EnumMember(Value = "phone_call")]
    PhoneCall
}
