namespace mediq.Application.Options;

/// <summary>
/// Selects + configures the integration-event broker seam and its drain worker. Dev/test default is the
/// honest no-op (<see cref="Provider"/> = "none" → <c>NullIntegrationEventBus</c>, drain worker OFF) so the
/// host runs end-to-end WITHOUT a configured/running broker — the durable outbox still CAPTURES every event.
/// Prod sets <see cref="Provider"/> = "rabbitmq" to wire the real publisher (same honest-stub seam as
/// <c>AiService:Provider</c> / the payout rail). The consumer is DEFERRED (not part of this slice).
/// </summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>"none" (dev/test default — NullIntegrationEventBus, no I/O) | "rabbitmq" (real publisher).</summary>
    public string Provider { get; set; } = "none";

    /// <summary>Durable topic exchange the publisher declares + publishes to (rabbitmq provider only).</summary>
    public string ExchangeName { get; set; } = "docslot.events";

    /// <summary>Exchange type (topic: routing key = event_type, so consumers bind by pattern).</summary>
    public string ExchangeType { get; set; } = "topic";

    /// <summary>Run the background drain loop in this instance. DEFAULT FALSE — the broker + consumer are deferred,
    /// so draining is opt-in even once a broker is configured (the outbox safely accumulates until then).</summary>
    public bool DrainWorkerEnabled { get; set; }

    /// <summary>Seconds between drain ticks.</summary>
    public int PollSeconds { get; set; } = 5;

    /// <summary>Max outbox rows claimed per tick.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Backoff base: a failed publish is rescheduled at now + BackoffBaseSeconds * 2^attempt_count, capped by BackoffMaxSeconds.</summary>
    public int BackoffBaseSeconds { get; set; } = 30;

    /// <summary>Backoff cap (seconds).</summary>
    public int BackoffMaxSeconds { get; set; } = 3600;

    /// <summary>Visibility lease (seconds): a claimed 'processing' row whose lease elapses (worker crashed mid-publish)
    /// is re-claimable on a later tick. Should comfortably exceed a single publish's timeout.</summary>
    public int LeaseSeconds { get; set; } = 300;

    /// <summary>Retries beyond the first attempt before a row dead-letters to 'abandoned'.</summary>
    public int MaxRetries { get; set; } = 8;
}
