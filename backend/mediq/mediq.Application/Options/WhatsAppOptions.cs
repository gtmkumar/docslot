namespace mediq.Application.Options;

/// <summary>
/// WhatsApp Cloud API inbound-webhook configuration, sourced from the "WhatsApp" config section (dev
/// defaults in appsettings.Development.json; supply via secrets/Aspire in prod).
/// <para>
/// SECURITY: <see cref="AppSecret"/> keys the HMAC-SHA256 verification of every inbound POST
/// (<c>X-Hub-Signature-256</c>), and <see cref="PhoneNumberIdToTenant"/> is the ONLY trusted source for
/// tenant resolution on the anonymous webhook — the payload's business phone-number id is mapped to a
/// tenant server-side, never taken from a client header.
/// </para>
/// </summary>
public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    /// <summary>Token Meta echoes during the GET verification handshake (<c>hub.verify_token</c>).</summary>
    public string VerifyToken { get; set; } = "docslot-verify";

    /// <summary>Meta App Secret — HMAC key for the <c>X-Hub-Signature-256</c> payload signature.</summary>
    public string AppSecret { get; set; } = "docslot-app-secret";

    /// <summary>
    /// Maps a WhatsApp Business <c>phone_number_id</c> (from <c>metadata.phone_number_id</c>) to the owning
    /// tenant. Unmapped ids are ignored (acknowledged with 200 but never processed) so a stray/spoofed
    /// number can't create rows under an unintended tenant.
    /// </summary>
    public Dictionary<string, Guid> PhoneNumberIdToTenant { get; set; } = new();
}
