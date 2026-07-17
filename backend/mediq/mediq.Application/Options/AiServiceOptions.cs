namespace mediq.Application.Options;

/// <summary>Selects + configures the AI sibling-service client. Dev/test default to a deterministic stub (the AI
/// service need not be running); prod sets 'http' with the AI service's base URL. The caller's bearer JWT is
/// forwarded to the AI service (it validates the same DocSlot JWT).</summary>
public sealed class AiServiceOptions
{
    public const string SectionName = "AiService";

    /// <summary>'stub' (dev/test default — deterministic local heuristic) | 'http' (call the real AI service).</summary>
    public string Provider { get; set; } = "stub";

    /// <summary>Base URL of the AI service (used only by the 'http' provider).</summary>
    public string BaseUrl { get; set; } = "http://localhost:8000";

    /// <summary>Per-call timeout for the 'http' provider (seconds) — kept short so a slow AI service never blocks the read.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    // --- Resilience (circuit breaker + bulkhead) for the 'http' provider. A degraded AI service must fail
    // fast for every caller rather than let each request thread/connection sit out the full timeout — these
    // are shared across all 4 AI typed clients (OCR/RAG/Triage/NoShow) since they all hit the same upstream.

    /// <summary>Max concurrent in-flight requests to the AI service across all 4 clients combined (bulkhead).
    /// Bounds how many requests can be waiting on a slow AI service at once, independent of the circuit breaker.</summary>
    public int MaxConcurrentRequests { get; set; } = 20;

    /// <summary>Requests queued once MaxConcurrentRequests is saturated before new ones are rejected outright.</summary>
    public int MaxQueuedRequests { get; set; } = 10;

    /// <summary>Fraction of requests in the sampling window that must fail (5xx/timeout/transport error) to trip the breaker.</summary>
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;

    /// <summary>Minimum requests in the sampling window before the failure ratio is evaluated (avoids tripping on a tiny sample).</summary>
    public int CircuitBreakerMinimumThroughput { get; set; } = 8;

    /// <summary>Rolling window (seconds) the failure ratio is evaluated over.</summary>
    public int CircuitBreakerSamplingSeconds { get; set; } = 30;

    /// <summary>How long (seconds) the breaker stays open — fast-failing every call — before half-opening to probe recovery.</summary>
    public int CircuitBreakerBreakSeconds { get; set; } = 30;
}
