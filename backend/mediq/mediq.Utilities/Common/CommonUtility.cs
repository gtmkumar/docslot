using System.Security.Cryptography;

namespace mediq.Utilities.Common;

public static class CommonUtility
{
    public static string GenerateOtp(int length = 6)
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), "OTP length must be greater than zero.");

        var max = (int)Math.Pow(10, length);
        var value = RandomNumberGenerator.GetInt32(0, max);
        return value.ToString().PadLeft(length, '0');
    }
}
