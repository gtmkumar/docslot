using System.Security.Cryptography;
using System.Text;
using mediq.Application.Abstractions;
using mediq.Application.Options;
using mediq.Utilities.Common;
using Microsoft.Extensions.Options;

namespace mediq.Infrastructure.PlatformApi;

/// <summary>
/// HMAC-SHA256 webhook signatures + at-rest protection of the signing secret. The subscriber recomputes
/// HMAC(payload, secret) and compares to <c>X-DocSlot-Signature: sha256=&lt;hex&gt;</c> (GitHub/Stripe style).
/// <para>
/// Because a webhook secret must be recoverable to sign each delivery, it is stored REVERSIBLY ENCRYPTED
/// (AES via the reused Utilities <see cref="EncryptionHelper"/>) — never plaintext, never a one-way hash.
/// </para>
/// </summary>
public sealed class WebhookSigner(IOptions<EncryptionOptions> encryption) : IWebhookSigner
{
    private readonly string _passphrase = encryption.Value.Passphrase;

    public string Sign(string payload, string signingSecret)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexString(mac).ToLowerInvariant()}";
    }

    public string ProtectSecret(string signingSecret) => EncryptionHelper.Encrypt(signingSecret, _passphrase);

    public string SignWithProtected(string payload, string protectedSecret)
    {
        var plaintext = EncryptionHelper.Decrypt(protectedSecret, _passphrase);
        return Sign(payload, plaintext);
    }
}
