using System.Security.Cryptography;
using System.Text;
using mediq.Application.Abstractions;
using mediq.Application.Options;
using Microsoft.Extensions.Options;

namespace mediq.Infrastructure.Docslot.WhatsApp;

/// <summary>
/// Verifies <c>X-Hub-Signature-256</c> on the inbound webhook: <c>sha256=&lt;hex&gt;</c> where hex is
/// HMAC-SHA256(rawBody) keyed by the Meta App Secret. The comparison is constant-time (no early-out timing
/// leak). A mismatch means the request is not from Meta → the controller rejects with 401.
/// </summary>
public sealed class WhatsAppSignatureVerifier(IOptions<WhatsAppOptions> options) : IWhatsAppSignatureVerifier
{
    private readonly WhatsAppOptions _options = options.Value;

    public bool Verify(byte[] rawBody, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader)) return false;

        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var providedHex = signatureHeader[prefix.Length..].Trim();

        byte[] provided;
        try { provided = Convert.FromHexString(providedHex); }
        catch (FormatException) { return false; }

        var key = Encoding.UTF8.GetBytes(_options.AppSecret);
        var expected = HMACSHA256.HashData(key, rawBody);

        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
