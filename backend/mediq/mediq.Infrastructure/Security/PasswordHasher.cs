using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using mediq.Application.Abstractions;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Verifies passwords against BOTH credential formats present in the platform:
/// <list type="bullet">
/// <item>bcrypt — what pgcrypto <c>crypt()</c> seeds produce (<c>$2a$ / $2b$ / $2y$</c>), verified via BCrypt.Net.</item>
/// <item>argon2id — the preferred format for newly-set passwords (security-hardening layer),
/// stored as <c>$argon2id$v=19$m=...,t=...,p=...$salt$hash</c> (PHC string).</item>
/// </list>
/// New hashes are always argon2id. Format is sniffed from the stored hash prefix.
/// </summary>
public sealed class PasswordHasher : IPasswordHasher
{
    private const int Argon2MemoryKb = 19_456;   // 19 MiB (OWASP argon2id baseline)
    private const int Argon2Iterations = 2;
    private const int Argon2Parallelism = 1;
    private const int Argon2SaltSize = 16;
    private const int Argon2HashSize = 32;

    public bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;

        if (storedHash.StartsWith("$argon2", StringComparison.Ordinal))
            return VerifyArgon2(password, storedHash);

        if (storedHash.StartsWith("$2", StringComparison.Ordinal))   // bcrypt $2a/$2b/$2y
            return BCrypt.Net.BCrypt.Verify(password, storedHash);

        // Unknown format → fail closed.
        return false;
    }

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(Argon2SaltSize);
        var hash = ComputeArgon2(password, salt);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"$argon2id$v=19$m={Argon2MemoryKb},t={Argon2Iterations},p={Argon2Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    private static bool VerifyArgon2(string password, string phc)
    {
        // $argon2id$v=19$m=..,t=..,p=..$<saltB64>$<hashB64>
        var parts = phc.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return false;

        var paramSection = parts[2]; // m=..,t=..,p=..
        int m = Argon2MemoryKb, t = Argon2Iterations, p = Argon2Parallelism;
        foreach (var kv in paramSection.Split(','))
        {
            var pair = kv.Split('=');
            if (pair.Length != 2) continue;
            switch (pair[0])
            {
                case "m": m = int.Parse(pair[1]); break;
                case "t": t = int.Parse(pair[1]); break;
                case "p": p = int.Parse(pair[1]); break;
            }
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expected = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException) { return false; }

        var actual = ComputeArgon2(password, salt, m, t, p, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] ComputeArgon2(
        string password, byte[] salt,
        int memoryKb = Argon2MemoryKb, int iterations = Argon2Iterations,
        int parallelism = Argon2Parallelism, int hashSize = Argon2HashSize)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(hashSize);
    }
}
