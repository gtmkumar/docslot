using System.Text;

namespace mediq.Utilities.Extensions;

public static class StringExtensions
{
    public static string RemoveSpecialCharacters(this string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(c);
        }

        return builder.ToString();
    }
}
