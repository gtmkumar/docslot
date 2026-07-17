using System.Security.Cryptography;
using System.Text;
using mediq.Application.Abstractions;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Mints the password-reset one-time bearer token (256 bits of CSPRNG entropy, URL-safe base64) and derives
/// its at-rest SHA-256 hash. Sibling of <see cref="InvitationTokenFactory"/> — the token is itself
/// high-entropy random, so SHA-256 (not a slow KDF) is the correct choice: there is nothing to brute-force and
/// the consume lookup stays a cheap, indexed equality. The plaintext leaves the process exactly once; only the
/// hash is stored.
/// </summary>
public sealed class PasswordResetTokenFactory : IPasswordResetTokenFactory
{
    private const int TokenBytes = 32;   // 256-bit token

    public (string Token, string TokenHash) Create()
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));
        return (token, Hash(token));
    }

    public string Hash(string token)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(digest);   // stable, case-insensitive hex; compared by exact string equality
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
