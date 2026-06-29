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
}
