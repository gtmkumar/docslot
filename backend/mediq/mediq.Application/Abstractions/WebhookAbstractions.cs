using mediq.Domain.PlatformApi;
using mediq.SharedDataModel.Docslot.PlatformApi;

namespace mediq.Application.Abstractions;

/// <summary>CRUD over <c>platform_api.webhook_subscriptions</c> + lookups for fan-out.</summary>
public interface IWebhookSubscriptionRepository
{
    Task<Guid> CreateAsync(CreateWebhookRequest request, string secretHash, DateTime nowUtc, CancellationToken ct);
    Task<WebhookSubscription?> GetByIdAsync(Guid webhookId, CancellationToken ct);
    Task<IReadOnlyList<WebhookSubscriptionDto>> ListByClientAsync(Guid clientId, CancellationToken ct);
    Task UpdateAsync(Guid webhookId, UpdateWebhookRequest request, CancellationToken ct);
    Task DeleteAsync(Guid webhookId, CancellationToken ct);

    /// <summary>Active subscriptions matching an event type (and tenant scope) — the delivery fan-out set.</summary>
    Task<IReadOnlyList<WebhookSubscription>> FindDeliverableAsync(string eventType, Guid? tenantId, CancellationToken ct);

    Task RecordOutcomeAsync(Guid webhookId, bool success, DateTime nowUtc, CancellationToken ct);
}

/// <summary>Reads the <c>platform_api.api_event_types</c> registry.</summary>
public interface IEventTypeRepository
{
    Task<IReadOnlyList<EventTypeDto>> ListAsync(CancellationToken ct);
    Task<bool> ExistsAndActiveAsync(string eventType, CancellationToken ct);
}

/// <summary>
/// Outbox for webhook deliveries (<c>platform_api.webhook_deliveries</c>). A delivery row is created in
/// 'pending', transitioned to success/failed/abandoned with the HMAC signature + response recorded, and
/// scheduled for retry via <c>next_retry_at</c>.
/// </summary>
public interface IWebhookDeliveryStore
{
    Task<Guid> EnqueueAsync(Guid webhookId, string eventType, Guid eventId, string payloadJson, DateTime nowUtc, CancellationToken ct);
    Task MarkSuccessAsync(Guid deliveryId, string signature, int statusCode, int responseMs, short attempt, DateTime nowUtc, CancellationToken ct);
    Task MarkFailedAsync(Guid deliveryId, string signature, int? statusCode, string error, short attempt, DateTime? nextRetryUtc, bool abandoned, DateTime nowUtc, CancellationToken ct);
    Task<WebhookDelivery?> GetAsync(Guid deliveryId, CancellationToken ct);
}

/// <summary>
/// Computes the HMAC-SHA256 signature a subscriber verifies (the canonical signing scheme), and protects
/// the signing secret at rest.
/// <para>
/// A webhook signing secret must be RECOVERABLE to sign each delivery, so (unlike passwords) it is stored
/// REVERSIBLY ENCRYPTED (AES, via <c>EncryptionHelper</c>) in <c>webhook_subscriptions.secret_hash</c> —
/// never plaintext. <see cref="ProtectSecret"/> encrypts for storage; <see cref="SignWithProtected"/>
/// decrypts then HMAC-signs in one step so the plaintext never lives beyond the call.
/// </para>
/// </summary>
public interface IWebhookSigner
{
    /// <summary>
    /// Returns the <c>X-DocSlot-Signature</c> value: <c>sha256=&lt;hex&gt;</c> of HMAC-SHA256(payload) keyed
    /// by the plaintext signing secret. Deterministic — the subscriber recomputes it.
    /// </summary>
    string Sign(string payload, string signingSecret);

    /// <summary>Encrypts a plaintext signing secret for at-rest storage.</summary>
    string ProtectSecret(string signingSecret);

    /// <summary>Decrypts a stored (protected) secret and HMAC-signs the payload with it.</summary>
    string SignWithProtected(string payload, string protectedSecret);
}

/// <summary>Performs the actual HTTP POST to a subscriber URL. Abstracted so tests inject a fake transport.</summary>
public interface IWebhookHttpDispatcher
{
    Task<WebhookHttpResult> PostAsync(string url, string payload, string signature, int timeoutSeconds, CancellationToken ct);
}

public sealed record WebhookHttpResult(bool Success, int? StatusCode, int ElapsedMs, string? Error);

/// <summary>
/// The publish→sign→deliver→retry pipeline. <see cref="PublishAsync"/> fans an integration event out to
/// every matching subscription, signs each payload, attempts delivery, and on failure schedules a Polly
/// backoff retry up to the subscription's max, dead-lettering ('abandoned') after exhaustion.
/// <para>
/// This is the integration-event seam: in slice 03 the docslot service raises domain events that are
/// translated to integration events at the Application boundary and published here (and onto RabbitMQ).
/// </para>
/// </summary>
public interface IWebhookPublisher
{
    Task<IReadOnlyList<Guid>> PublishAsync(IntegrationEvent integrationEvent, CancellationToken ct);
}

/// <summary>
/// A platform integration event (crosses the service boundary). <see cref="EventId"/> is the idempotency
/// key carried into <c>webhook_deliveries.event_id</c>; <see cref="CorrelationId"/> flows from the inbound
/// request (and, later, RabbitMQ headers).
/// </summary>
public sealed record IntegrationEvent(
    Guid EventId,
    string EventType,           // 'docslot.booking.created'
    Guid? TenantId,
    string PayloadJson,
    string? CorrelationId,
    DateTime OccurredAtUtc);
