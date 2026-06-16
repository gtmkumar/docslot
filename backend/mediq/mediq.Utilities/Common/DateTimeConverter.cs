using System.Globalization;

namespace mediq.Utilities.Common;

public static class DateTimeConverter
{
    private static readonly string[] AcceptedSeparators = { ".", "/", "-", " " };

    public enum DateFormat
    {
        DDMMYY,
        MMDDYY,
        YYMMDD
    }

    public static bool TryParse(string? date, DateFormat format, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(date))
            return false;

        foreach (var separator in AcceptedSeparators)
        {
            var pattern = format switch
            {
                DateFormat.DDMMYY => $"dd{separator}MM{separator}yyyy",
                DateFormat.MMDDYY => $"MM{separator}dd{separator}yyyy",
                DateFormat.YYMMDD => $"yyyy{separator}MM{separator}dd",
                _ => null
            };

            if (pattern is not null &&
                DateTime.TryParseExact(date, pattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            {
                return true;
            }
        }

        return false;
    }

    public static string? Normalize(string? date, DateFormat format, string separator = "-")
    {
        return TryParse(date, format, out var parsed)
            ? parsed.ToString(format switch
            {
                DateFormat.DDMMYY => $"dd{separator}MM{separator}yyyy",
                DateFormat.MMDDYY => $"MM{separator}dd{separator}yyyy",
                DateFormat.YYMMDD => $"yyyy{separator}MM{separator}dd",
                _ => CultureInfo.InvariantCulture.DateTimeFormat.ShortDatePattern
            }, CultureInfo.InvariantCulture)
            : null;
    }
}
