namespace mediq.Domain.Docslot;

/// <summary>
/// Booking lifecycle status — the exact 7 tokens of the <c>docslot.bookings.status</c> CHECK constraint
/// (database/03_docslot.sql D10): pending, confirmed, checked_in, cancelled, completed, no_show, rescheduled.
/// Stored/compared as the snake_case string; this enum is the domain-internal representation.
/// </summary>
public enum BookingStatus
{
    Pending,
    Confirmed,
    CheckedIn,
    Cancelled,
    Completed,
    NoShow,
    Rescheduled,
}

public static class BookingStatusTokens
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string CheckedIn = "checked_in";
    public const string Cancelled = "cancelled";
    public const string Completed = "completed";
    public const string NoShow = "no_show";
    public const string Rescheduled = "rescheduled";

    public static string ToToken(this BookingStatus s) => s switch
    {
        BookingStatus.Pending => Pending,
        BookingStatus.Confirmed => Confirmed,
        BookingStatus.CheckedIn => CheckedIn,
        BookingStatus.Cancelled => Cancelled,
        BookingStatus.Completed => Completed,
        BookingStatus.NoShow => NoShow,
        BookingStatus.Rescheduled => Rescheduled,
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static BookingStatus FromToken(string token) => token switch
    {
        Pending => BookingStatus.Pending,
        Confirmed => BookingStatus.Confirmed,
        CheckedIn => BookingStatus.CheckedIn,
        Cancelled => BookingStatus.Cancelled,
        Completed => BookingStatus.Completed,
        NoShow => BookingStatus.NoShow,
        Rescheduled => BookingStatus.Rescheduled,
        _ => throw new ArgumentException($"Unknown booking status token '{token}'."),
    };
}

/// <summary>
/// <c>docslot.bookings.booked_by_type</c> tokens: who placed the booking relative to the patient.
/// </summary>
public static class BookedByType
{
    public const string Self = "self";
    public const string Behalf = "behalf";
}

/// <summary>
/// <c>docslot.bookings.patient_consent_status</c> tokens (DPDP behalf-booking consent). Self bookings are
/// <c>not_required</c>; behalf bookings start <c>pending</c> and become <c>confirmed</c>/<c>denied</c> via the
/// patient's WhatsApp OTP reply, or <c>expired</c> if the code lapses (swept by the maintenance worker).
/// </summary>
public static class PatientConsentStatus
{
    public const string NotRequired = "not_required";
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Denied = "denied";
    public const string Expired = "expired";
}

/// <summary>
/// <c>docslot.bookings.behalf_relation</c> tokens — the booker's claimed relation to the patient on a behalf
/// booking. Mirrors the DB CHECK in 09_chat_identity.sql.
/// </summary>
public static class BehalfRelation
{
    public const string Family = "family";
    public const string Friend = "friend";
    public const string Neighbour = "neighbour";
    public const string CarePartner = "care_partner";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string> { Family, Friend, Neighbour, CarePartner, Other };

    public static bool IsValid(string? relation) => relation is not null && All.Contains(relation);
}
