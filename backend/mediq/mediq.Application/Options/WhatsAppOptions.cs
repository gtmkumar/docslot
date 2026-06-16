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

    // --- Outbound delivery (the outbox drain worker) -------------------------------------------------

    /// <summary>
    /// Master switch for the <c>OutboxDrainWorker</c> hosted service. Default true; set false to disable the
    /// background drain (e.g. a read-only / API-only instance, or when an external dispatcher owns the outbox).
    /// </summary>
    public bool OutboxWorkerEnabled { get; set; } = true;

    /// <summary>How often the drain worker polls for due 'pending' messages, in seconds (default ~5s).</summary>
    public int OutboxPollSeconds { get; set; } = 5;

    /// <summary>Max number of due messages claimed and sent per poll iteration (keeps a tick bounded).</summary>
    public int OutboxBatchSize { get; set; } = 20;

    /// <summary>
    /// Base delay (seconds) for the exponential retry backoff applied to a failed send. The next attempt is
    /// scheduled at <c>now + BackoffBaseSeconds * 2^(attempt_count-1)</c>, capped by <see cref="BackoffMaxSeconds"/>.
    /// </summary>
    public int BackoffBaseSeconds { get; set; } = 30;

    /// <summary>Upper bound (seconds) on the computed exponential backoff between retries.</summary>
    public int BackoffMaxSeconds { get; set; } = 3600;

    /// <summary>
    /// Meta Graph API base URL for the real sender (e.g. <c>https://graph.facebook.com/v21.0</c>). When this
    /// and <see cref="AccessToken"/> are BOTH set, the real <c>MetaWhatsAppSender</c> is selected; otherwise
    /// the dev <c>StubWhatsAppSender</c> is used (logs the send, never calls Meta).
    /// </summary>
    public string? GraphBaseUrl { get; set; }

    /// <summary>
    /// WhatsApp Cloud API permanent/system-user access token (Bearer). Presence selects the real sender; in
    /// dev/secret-less environments it is null and the stub is used. NEVER hardcode — supply via secrets/Aspire.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// The business <c>phone_number_id</c> the real sender POSTs through (<c>/{phone_number_id}/messages</c>).
    /// Only required for the real Meta sender.
    /// </summary>
    public string? SenderPhoneNumberId { get; set; }
}
