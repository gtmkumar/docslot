using mediq.SharedDataModel.Docslot.Dashboard.Enums;

namespace mediq.Infrastructure.Docslot;

/// <summary>
/// PHI guard: masks a phone number for list/queue read-models. The raw number is NEVER serialized into a
/// list (DPDP); revealing it is a separate purpose-of-use-gated read. Keeps the country code + last 2 digits.
/// </summary>
internal static class PhoneMasker
{
    public static string Mask(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var digits = raw.Trim();
        if (digits.Length <= 4) return new string('x', digits.Length);
        var prefix = digits[..Math.Min(3, digits.Length)];
        var last2 = digits[^2..];
        return $"{prefix}{new string('x', Math.Max(0, digits.Length - 5))}{last2}";
    }
}

/// <summary>
/// Maps DB snake_case tokens to the wire enums (which carry the same tokens via [EnumMember]). Unknown
/// values are tolerated defensively (Language) per the contract.
/// </summary>
internal static class EnumParse
{
    // Covers every token the bookings.status CHECK allows. The default must NOT silently fall back to
    // Pending: a missing arm once made checked-in patients reappear in the approval queue (Approve → 422).
    public static BookingStatus Status(string token) => token switch
    {
        "pending" => BookingStatus.Pending,
        "confirmed" => BookingStatus.Confirmed,
        "checked_in" => BookingStatus.CheckedIn,
        "cancelled" => BookingStatus.Cancelled,
        "completed" => BookingStatus.Completed,
        "no_show" => BookingStatus.NoShow,
        "rescheduled" => BookingStatus.Rescheduled,
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, "Unknown booking status token."),
    };

    public static BookingSource Source(string token) => token switch
    {
        "whatsapp" => BookingSource.Whatsapp,
        "dashboard" => BookingSource.Dashboard,
        "api" => BookingSource.Api,
        "walk_in" => BookingSource.WalkIn,
        "phone_call" => BookingSource.PhoneCall,
        _ => BookingSource.Dashboard,
    };

    public static Gender? Gender(string? token) => token switch
    {
        "male" => SharedDataModel.Docslot.Dashboard.Enums.Gender.Male,
        "female" => SharedDataModel.Docslot.Dashboard.Enums.Gender.Female,
        "other" => SharedDataModel.Docslot.Dashboard.Enums.Gender.Other,
        "prefer_not_say" => SharedDataModel.Docslot.Dashboard.Enums.Gender.PreferNotSay,
        _ => null,
    };

    public static Language? Language(string? token) => token switch
    {
        "en" => SharedDataModel.Docslot.Dashboard.Enums.Language.English,
        "hi" => SharedDataModel.Docslot.Dashboard.Enums.Language.Hindi,
        _ => null,   // tolerate unknown language codes
    };
}
