using System.Text;
using System.Text.Json;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Docslot.WhatsApp;
using mediq.Application.Options;
using mediq.SharedDataModel.Docslot.WhatsApp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace mediq.Api.Controllers;

/// <summary>
/// Inbound WhatsApp Cloud API webhook (Meta). Both actions are ANONYMOUS: Meta calls them without a JWT.
/// Trust is established differently from the rest of the API:
/// <list type="bullet">
/// <item>GET handshake echoes <c>hub.challenge</c> only when <c>hub.verify_token</c> matches our secret.</item>
/// <item>POST verifies the <c>X-Hub-Signature-256</c> HMAC over the EXACT raw body (App Secret keyed) — a
/// mismatch is 401, so only Meta can drive the booking flow.</item>
/// <item>The tenant is resolved server-side from the trusted <c>phone_number_id</c> map (never a header) and
/// pushed into <see cref="ITenantScopeOverride"/> so the booking pipeline scopes RLS + creates the booking
/// under the right tenant.</item>
/// </list>
/// The controller stays thin: verify → parse → per message dispatch <see cref="ProcessInboundWhatsAppMessageCommand"/>.
/// It always returns 200 quickly on a well-formed (signed) request so Meta does not retry-storm.
/// </summary>
[ApiController]
[Route("api/v1/whatsapp")]
public sealed class WhatsAppController(
    ICommandDispatcher commands,
    IWhatsAppSignatureVerifier signatureVerifier,
    ITenantScopeOverride tenantScope,
    IOptions<WhatsAppOptions> options,
    ILogger<WhatsAppController> logger) : ControllerBase
{
    private readonly WhatsAppOptions _options = options.Value;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// GET /api/v1/whatsapp/webhook — Meta verification handshake. Returns the raw <c>hub.challenge</c> as
    /// text/plain (200) when mode == subscribe and the verify token matches; otherwise 403.
    /// </summary>
    [HttpGet("webhook")]
    [AllowAnonymous]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (mode == "subscribe" && verifyToken == _options.VerifyToken)
            return Content(challenge ?? string.Empty, "text/plain");

        logger.LogWarning("WhatsApp webhook verification failed (mode={Mode}).", mode);
        return StatusCode(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// POST /api/v1/whatsapp/webhook — inbound message delivery. Verifies the HMAC signature over the raw
    /// body, maps phone_number_id → tenant, then dispatches one command per message. Always 200 on a valid
    /// signed payload (even if a message is for an unmapped number — acknowledged but ignored).
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        // Read the EXACT raw bytes for HMAC — must match what Meta signed, byte-for-byte.
        var rawBody = await ReadRawBodyAsync(ct);

        var signature = Request.Headers["X-Hub-Signature-256"].ToString();
        if (!signatureVerifier.Verify(rawBody, signature))
        {
            logger.LogWarning("WhatsApp webhook signature verification failed.");
            return Unauthorized();
        }

        WhatsAppWebhookEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<WhatsAppWebhookEnvelope>(rawBody, Json);
        }
        catch (JsonException ex)
        {
            // Signed-but-unparseable: ack so Meta doesn't retry-storm; nothing to process.
            logger.LogWarning(ex, "WhatsApp webhook payload could not be parsed.");
            return Ok();
        }

        foreach (var change in EnumerateChanges(envelope))
        {
            var phoneNumberId = change.Metadata?.PhoneNumberId;
            if (phoneNumberId is null || !_options.PhoneNumberIdToTenant.TryGetValue(phoneNumberId, out var tenantId))
            {
                logger.LogWarning("WhatsApp message for unmapped phone_number_id {PhoneNumberId}; ignoring.", phoneNumberId);
                continue;   // unmapped number → acknowledge but don't process
            }

            // Establish the tenant scope for the (anonymous) booking pipeline — server-trusted, not a header.
            tenantScope.Set(tenantId);

            var displayName = change.Contacts?.FirstOrDefault()?.Profile?.Name;

            foreach (var message in change.Messages ?? [])
            {
                if (message.Id is null || message.From is null) continue;

                var (type, body) = ExtractText(message);
                try
                {
                    await commands.Send(new ProcessInboundWhatsAppMessageCommand(
                        tenantId, message.Id, message.From, type, body, displayName), ct);
                }
                catch (Exception ex)
                {
                    // One bad message must not fail the whole batch / trigger a Meta retry-storm.
                    logger.LogError(ex, "Failed to process WhatsApp message {MessageId}.", message.Id);
                }
            }
        }

        return Ok();
    }

    private async Task<byte[]> ReadRawBodyAsync(CancellationToken ct)
    {
        Request.EnableBuffering();
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        Request.Body.Position = 0;
        return ms.ToArray();
    }

    private static IEnumerable<WhatsAppChangeValue> EnumerateChanges(WhatsAppWebhookEnvelope? envelope)
    {
        if (envelope?.Entry is null) yield break;
        foreach (var entry in envelope.Entry)
        {
            if (entry.Changes is null) continue;
            foreach (var change in entry.Changes)
                if (change.Value is not null)
                    yield return change.Value;
        }
    }

    /// <summary>Pulls the user's reply text from a text or interactive (button/list) message.</summary>
    private static (string Type, string? Body) ExtractText(WhatsAppMessage message)
    {
        if (string.Equals(message.Type, "interactive", StringComparison.OrdinalIgnoreCase))
        {
            var reply = message.Interactive?.ButtonReply ?? message.Interactive?.ListReply;
            return ("interactive", reply?.Id ?? reply?.Title);
        }
        return ("text", message.Text?.Body);
    }
}
