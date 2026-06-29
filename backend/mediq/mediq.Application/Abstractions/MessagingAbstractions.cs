namespace mediq.Application.Abstractions;

/// <summary>
/// The broker publish seam for the durable integration-event outbox. The drain worker resolves this and
/// publishes each claimed outbox row onto the message bus. Two implementations: a <c>NullIntegrationEventBus</c>
/// (dev/test default, Messaging:Provider=none — a no-op so the host runs without a broker) and a
/// <c>RabbitMqIntegrationEventBus</c> (Messaging:Provider=rabbitmq — declares the topic exchange and publishes
/// with confirms). NO consumer is part of this slice (deferred): events fan out to the broker, not per-subscriber.
/// </summary>
public interface IIntegrationEventBus
{
    /// <summary>
    /// Publishes one integration event to the broker. <paramref name="eventType"/> is the routing key;
    /// <paramref name="eventId"/> / <paramref name="correlationId"/> / <paramref name="tenantId"/> ride as
    /// message headers so a downstream consumer can dedup and correlate. Throws on a publish failure (the
    /// drain worker turns a throw into a retry/backoff or dead-letter decision via the drain store).
    /// </summary>
    Task PublishAsync(
        Guid eventId,
        string eventType,
        Guid? tenantId,
        string payloadJson,
        string? correlationId,
        DateTime occurredAtUtc,
        CancellationToken ct);
}

/// <summary>
/// The transactional CAPTURE half of the integration-event outbox. Called inside the command's ambient
/// UnitOfWork transaction (from <c>WebhookPublisher.PublishAsync</c>), so the outbox row is written ATOMICALLY
/// with the business write — if the command rolls back, the row never persists. This closes the lost-event gap:
/// every event is captured here regardless of whether any webhook subscription matches.
/// </summary>
public interface IIntegrationOutboxStore
{
    /// <summary>Inserts a 'pending' outbox row for the event (idempotent: <c>ON CONFLICT (event_id) DO NOTHING</c>).</summary>
    Task RecordAsync(IntegrationEvent evt, CancellationToken ct);
}

/// <summary>
/// The drain half of the integration-event outbox — the worker side. Publishing only CAPTURES a 'pending' row
/// (<see cref="IIntegrationOutboxStore"/>); the <c>IntegrationEventDrainWorker</c> claims due rows here, publishes
/// them via <see cref="IIntegrationEventBus"/>, and records the outcome. Mirrors the webhook drain store: the
/// platform_api tables are NOT RLS-protected, so the claim/mark run as plain app-role SQL (no SECURITY DEFINER);
/// the claim uses <c>FOR UPDATE SKIP LOCKED</c> + a visibility lease on <c>next_retry_at</c> so scaled-out workers
/// never double-publish and a crashed worker's 'processing' row is re-claimable.
/// </summary>
public interface IIntegrationEventOutboxDrainStore
{
    /// <summary>Atomically claims up to <paramref name="batchSize"/> DUE rows (pending / failed-past-backoff /
    /// stranded-processing-past-lease), flips each to 'processing' with a fresh <paramref name="leaseSeconds"/>
    /// lease, and returns them. NO subscription join — one row per event, fanned to the broker.</summary>
    Task<IReadOnlyList<ClaimedIntegrationEvent>> ClaimDueAsync(int batchSize, int leaseSeconds, DateTime nowUtc, CancellationToken ct);

    /// <summary>Single-winner success: → 'success', attempt++, published_at set — but ONLY if the row is still
    /// 'processing' (a stale call on a re-claimed row is a no-op).</summary>
    Task MarkPublishedAsync(Guid outboxId, DateTime nowUtc, CancellationToken ct);

    /// <summary>Single-winner failure: attempt++, then 'abandoned' at <paramref name="maxRetries"/> (next_retry_at
    /// cleared) else 'failed' + the backoff <paramref name="nextRetryUtc"/> — but ONLY if the row is still
    /// 'processing'.</summary>
    Task MarkFailedAsync(Guid outboxId, string error, int maxRetries, DateTime nextRetryUtc, DateTime nowUtc, CancellationToken ct);
}

/// <summary>A claimed outbox row the worker publishes to the broker. <paramref name="AttemptCount"/> is the
/// pre-publish count (backs the worker's exponential backoff on failure).</summary>
public sealed record ClaimedIntegrationEvent(
    Guid OutboxId,
    Guid EventId,
    string EventType,
    Guid? TenantId,
    string PayloadJson,
    string? CorrelationId,
    DateTime OccurredAt,
    int AttemptCount);
