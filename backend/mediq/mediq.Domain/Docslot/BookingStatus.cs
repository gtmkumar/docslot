namespace mediq.Domain.Docslot;

/// <summary>
/// Booking lifecycle status — the exact 6 tokens of the <c>docslot.bookings.status</c> CHECK constraint
/// (database/03_docslot.sql D10): pending, confirmed, cancelled, completed, no_show, rescheduled.
/// Stored/compared as the snake_case string; this enum is the domain-internal representation.
/// </summary>
public enum BookingStatus
{
    Pending,
    Confirmed,
    Cancelled,
    Completed,
    NoShow,
    Rescheduled,
}

public static class BookingStatusTokens
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Cancelled = "cancelled";
    public const string Completed = "completed";
    public const string NoShow = "no_show";
    public const string Rescheduled = "rescheduled";

    public static string ToToken(this BookingStatus s) => s switch
    {
        BookingStatus.Pending => Pending,
        BookingStatus.Confirmed => Confirmed,
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
        Cancelled => BookingStatus.Cancelled,
        Completed => BookingStatus.Completed,
        NoShow => BookingStatus.NoShow,
        Rescheduled => BookingStatus.Rescheduled,
        _ => throw new ArgumentException($"Unknown booking status token '{token}'."),
    };
}
