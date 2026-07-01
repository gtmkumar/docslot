using System.Security.Cryptography;
using System.Text;
using mediq.Application.Abstractions;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Mints the invitation's one-time bearer token (256 bits of CSPRNG entropy, URL-safe base64) and derives
/// its at-rest SHA-256 hash. The plaintext leaves the process exactly once (in the create/resend response);
/// only the hash is stored. SHA-256 (not a slow KDF) is the right choice here because the token is itself
/// high-entropy random — there is nothing to brute-force — so the lookup stays a cheap, indexed equality.
/// </summary>
public sealed class InvitationTokenFactory : IInvitationTokenFactory
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
