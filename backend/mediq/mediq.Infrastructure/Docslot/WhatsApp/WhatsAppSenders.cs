using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using mediq.Application.Abstractions;
using mediq.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace mediq.Infrastructure.Docslot.WhatsApp;

/// <summary>
/// Dev/default outbound transport. Does NOT call Meta — it logs the intended send and returns a synthetic
/// provider id (<c>wamid.stub.&lt;guid&gt;</c>) so the drain worker can flip the row to 'sent' and the whole
/// outbound path is exercisable end-to-end without a WhatsApp Cloud API credential. Selected whenever
/// <c>WhatsApp:AccessToken</c> / <c>WhatsApp:GraphBaseUrl</c> are not configured.
/// </summary>
public sealed class StubWhatsAppSender(ILogger<StubWhatsAppSender> logger) : IWhatsAppSender
{
    public Task<WhatsAppSendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var providerMessageId = $"wamid.stub.{Guid.NewGuid():N}";

        // Structured, but do NOT log the message body at info level — it can be PHI-adjacent (patient name,
        // appointment details). Intent + tenant + the synthetic id are enough to trace the send.
        logger.LogInformation(
            "StubWhatsAppSender: simulated send outbox={OutboxId} tenant={TenantId} intent={Intent} to={To} → {ProviderMessageId}",
            message.OutboxId, message.TenantId, message.MessageIntent, Mask(message.ToPhone), providerMessageId);

        return Task.FromResult(WhatsAppSendResult.Sent(providerMessageId));
    }

    /// <summary>Mask all but the last 4 digits of the destination so logs don't leak the full number.</summary>
    private static string Mask(string phone) =>
        phone.Length <= 4 ? "****" : new string('*', phone.Length - 4) + phone[^4..];
}

/// <summary>
/// Real WhatsApp Cloud API transport: POST <c>{GraphBaseUrl}/{SenderPhoneNumberId}/messages</c> with a Bearer
/// access token. Wired ONLY when <c>WhatsApp:AccessToken</c> + <c>WhatsApp:GraphBaseUrl</c> are configured
/// (see <see cref="InfrastructureRegistration"/>). The drain worker owns retry/backoff, so this returns a
/// plain success/failure for one attempt rather than retrying internally.
/// </summary>
public sealed class MetaWhatsAppSender(
    HttpClient http,
    IOptions<WhatsAppOptions> options,
    ILogger<MetaWhatsAppSender> logger) : IWhatsAppSender
{
    private readonly WhatsAppOptions _options = options.Value;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<WhatsAppSendResult> SendAsync(OutboundMessage message, CancellationToken ct)
    {
        var baseUrl = _options.GraphBaseUrl!.TrimEnd('/');
        var phoneNumberId = _options.SenderPhoneNumberId
            ?? throw new InvalidOperationException("WhatsApp:SenderPhoneNumberId is required for the real Meta sender.");

        // WhatsApp Cloud API text-message envelope. (Template/interactive payloads can be added later by
        // inspecting message.MessageIntent and the outbox payload; this base path sends free-form text.)
        var body = JsonSerializer.Serialize(new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = message.ToPhone,
            type = "text",
            text = new { preview_url = false, body = message.Text },
        }, Json);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/{phoneNumberId}/messages")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

        try
        {
            using var resp = await http.SendAsync(req, ct);
            var responseBody = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return WhatsAppSendResult.Failed($"graph {(int)resp.StatusCode}: {Truncate(responseBody)}");

            // { "messages": [ { "id": "wamid...." } ] }
            using var doc = JsonDocument.Parse(responseBody);
            var providerMessageId = doc.RootElement
                .GetProperty("messages")[0]
                .GetProperty("id")
                .GetString();

            return providerMessageId is { Length: > 0 }
                ? WhatsAppSendResult.Sent(providerMessageId)
                : WhatsAppSendResult.Failed("graph 200 but no message id in response");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "MetaWhatsAppSender send failed for outbox={OutboxId}", message.OutboxId);
            return WhatsAppSendResult.Failed(ex.Message);
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
