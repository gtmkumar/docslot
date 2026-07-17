namespace mediq.Application.Options;

/// <summary>Configures the durable async webhook-delivery worker. Publishing only enqueues a 'pending'
/// delivery; this worker drains + signs + POSTs + retries out-of-band, so a slow/dead subscriber never blocks
/// the request path. Disable the background loop (read-only / API-only instances) via DeliveryWorkerEnabled=false.</summary>
public sealed class WebhookDeliveryOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>Run the background delivery loop in this instance (default true; false for the test host / API-only nodes).</summary>
    public bool DeliveryWorkerEnabled { get; set; } = true;

    /// <summary>Seconds between drain ticks.</summary>
    public int PollSeconds { get; set; } = 5;

    /// <summary>Max deliveries claimed per tick.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Backoff base: a failed delivery is rescheduled at now + BackoffBaseSeconds * 2^attempt_count, capped by BackoffMaxSeconds.</summary>
    public int BackoffBaseSeconds { get; set; } = 30;

    /// <summary>Backoff cap (seconds).</summary>
    public int BackoffMaxSeconds { get; set; } = 3600;

    /// <summary>Visibility lease (seconds): a claimed 'processing' row whose lease elapses (worker crashed mid-delivery)
    /// is re-claimable on a later tick. Should comfortably exceed a single delivery's timeout.</summary>
    public int LeaseSeconds { get; set; } = 300;

    /// <summary>Consecutive failures after which a subscription is auto-disabled (stops further delivery attempts).</summary>
    public int AutoDisableThreshold { get; set; } = 20;

    // --- Resilience (circuit breaker + bulkhead). Subscriber URLs are arbitrary third-party endpoints
    // belonging to many different tenants — one dead/slow subscriber must never stall delivery to everyone
    // else's healthy subscriptions in the same batch.

    /// <summary>Max deliveries processed concurrently within one drain tick (overall bulkhead for this batch).</summary>
    public int MaxDegreeOfParallelism { get; set; } = 8;

    /// <summary>Max concurrent in-flight deliveries to any ONE destination host (per-host bulkhead) —
    /// independent of MaxDegreeOfParallelism, so one host can't consume the whole batch's concurrency budget.</summary>
    public int PerHostMaxConcurrent { get; set; } = 3;

    /// <summary>Fraction of attempts to a given host, within the sampling window, that must fail to trip that host's breaker.</summary>
    public double PerHostCircuitBreakerFailureRatio { get; set; } = 0.5;

    /// <summary>Minimum attempts to a given host in the window before its failure ratio is evaluated.</summary>
    public int PerHostCircuitBreakerMinimumThroughput { get; set; } = 4;

    /// <summary>Rolling window (seconds) a host's failure ratio is evaluated over.</summary>
    public int PerHostCircuitBreakerSamplingSeconds { get; set; } = 60;

    /// <summary>How long (seconds) a host's breaker stays open — fast-failing every delivery to it — before probing recovery.</summary>
    public int PerHostCircuitBreakerBreakSeconds { get; set; } = 30;
}
