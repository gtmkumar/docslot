using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace mediq.Application.Common;

/// <summary>
/// Shared one-time-code crypto for every OTP surface (behalf-booking consent + post-hoc attribution claim).
/// A code is NEVER stored — only <c>base64(sha256(salt || ":" || code))</c>; verification is constant-time.
/// Single source of truth so all OTP flows use identical, audited crypto (no per-feature divergence).
/// </summary>
public static class OtpCodec
{
    public const int CodeLength = 6;

    /// <summary>A cryptographically-random 6-digit code (zero-padded).</summary>
    public static string GenerateCode() =>
        RandomNumberGenerator.GetInt32(0, (int)Math.Pow(10, CodeLength))
            .ToString("D" + CodeLength, CultureInfo.InvariantCulture);

    /// <summary>A fresh per-row random salt (base64, 16 bytes).</summary>
    public static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    /// <summary>base64( sha256( salt || ":" || code ) ) — the only representation of a code that is ever stored.</summary>
    public static string Hash(string salt, string code) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + code)));

    /// <summary>Constant-time check of a submitted code against the stored salt + digest.</summary>
    public static bool Matches(string salt, string expectedHash, string submittedCode) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(Hash(salt, submittedCode)),
            Convert.FromBase64String(expectedHash));
}
