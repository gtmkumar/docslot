using System.Diagnostics;
using System.Text;
using mediq.Application.Abstractions;

namespace mediq.Infrastructure.PlatformApi;

/// <summary>
/// Real HTTP transport for webhook delivery. POSTs the JSON payload with the HMAC signature header and a
/// per-subscription timeout. Any non-2xx, timeout, or transport error is a non-success result the publisher
/// will retry. Abstracted behind <see cref="IWebhookHttpDispatcher"/> so tests inject a fake.
/// </summary>
public sealed class WebhookHttpDispatcher(IHttpClientFactory httpClientFactory) : IWebhookHttpDispatcher
{
    public const string SignatureHeader = "X-DocSlot-Signature";
    public const string EventHeader = "X-DocSlot-Event-Id";

    public async Task<WebhookHttpResult> PostAsync(string url, string payload, string signature, int timeoutSeconds, CancellationToken ct)
    {
        var sw = Stopwatch.GetTimestamp();
        try
        {
            using var client = httpClientFactory.CreateClient("webhooks");
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60));

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            content.Headers.Add(SignatureHeader, signature);

            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            using var response = await client.SendAsync(request, ct);

            var elapsed = (int)Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
            return response.IsSuccessStatusCode
                ? new WebhookHttpResult(true, (int)response.StatusCode, elapsed, null)
                : new WebhookHttpResult(false, (int)response.StatusCode, elapsed, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            var elapsed = (int)Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
            return new WebhookHttpResult(false, null, elapsed, ex.Message);
        }
    }
}
