using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using mediq.Application.Abstractions;
using mediq.Application.Options;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;

namespace mediq.Infrastructure.PlatformApi;

/// <summary>
/// Real HTTP transport for webhook delivery. POSTs the JSON payload with the HMAC signature header and a
/// per-subscription timeout. Any non-2xx, timeout, or transport error is a non-success result the publisher
/// will retry. Abstracted behind <see cref="IWebhookHttpDispatcher"/> so tests inject a fake.
///
/// Subscriber URLs are arbitrary third-party endpoints belonging to many different tenants, so resilience is
/// scoped PER DESTINATION HOST: a circuit breaker + concurrency limiter keyed by <c>Uri.Host</c> means a
/// persistently dead/slow subscriber fast-fails (instead of burning its full per-call timeout on every
/// attempt) WITHOUT tripping delivery to any other tenant's healthy subscriber. Registered as a singleton
/// (see AddInfrastructure) so each host's breaker state survives across worker ticks — a scoped instance would
/// reset every tick and never actually stay "open".
/// </summary>
public sealed class WebhookHttpDispatcher(IHttpClientFactory httpClientFactory, IOptions<WebhookDeliveryOptions> options)
    : IWebhookHttpDispatcher
{
    public const string SignatureHeader = "X-DocSlot-Signature";
    public const string EventHeader = "X-DocSlot-Event-Id";

    private readonly WebhookDeliveryOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, ResiliencePipeline<HttpResponseMessage>> _hostPipelines = new();

    public async Task<WebhookHttpResult> PostAsync(string url, string payload, string signature, int timeoutSeconds, CancellationToken ct)
    {
        var sw = Stopwatch.GetTimestamp();
        try
        {
            // Authority (host:port), NOT just Host — two distinct subscriber endpoints can share a host on
            // different ports (or the same origin serving different tenants' webhook paths), and keying by
            // Host alone would wrongly merge their breaker/bulkhead state.
            var authority = new Uri(url).Authority;
            var pipeline = _hostPipelines.GetOrAdd(authority, BuildHostPipeline);

            using var client = httpClientFactory.CreateClient("webhooks");
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60));

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            content.Headers.Add(SignatureHeader, signature);

            using var response = await pipeline.ExecuteAsync(
                async token =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                    return await client.SendAsync(request, token);
                },
                ct);

            var elapsed = (int)Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
            return response.IsSuccessStatusCode
                ? new WebhookHttpResult(true, (int)response.StatusCode, elapsed, null)
                : new WebhookHttpResult(false, (int)response.StatusCode, elapsed, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            // Covers transport errors, the per-call timeout above, AND a tripped breaker
            // (BrokenCircuitException) / saturated bulkhead (RateLimiterRejectedException) — all become an
            // ordinary failed WebhookHttpResult, so the worker's existing retry/backoff/dead-letter logic
            // handles them exactly like any other delivery failure with no special-casing needed.
            var elapsed = (int)Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
            return new WebhookHttpResult(false, null, elapsed, ex.Message);
        }
    }

    private ResiliencePipeline<HttpResponseMessage> BuildHostPipeline(string authority) =>
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddConcurrencyLimiter(Math.Max(1, _options.PerHostMaxConcurrent), queueLimit: 0)
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = _options.PerHostCircuitBreakerFailureRatio,
                MinimumThroughput = Math.Max(2, _options.PerHostCircuitBreakerMinimumThroughput),
                SamplingDuration = TimeSpan.FromSeconds(Math.Max(1, _options.PerHostCircuitBreakerSamplingSeconds)),
                BreakDuration = TimeSpan.FromSeconds(Math.Max(1, _options.PerHostCircuitBreakerBreakSeconds)),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()   // client.Timeout firing surfaces as this, not Polly's own TimeoutRejectedException
                    .HandleResult(r => (int)r.StatusCode >= 500),
            })
            .Build();
}
