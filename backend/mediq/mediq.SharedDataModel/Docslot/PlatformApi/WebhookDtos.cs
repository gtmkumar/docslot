namespace mediq.SharedDataModel.Docslot.PlatformApi;

/// <summary>Webhook subscription (maps to <c>platform_api.webhook_subscriptions</c>). NEVER carries the secret.</summary>
public sealed record WebhookSubscriptionDto(
    Guid WebhookId,
    Guid ClientId,
    Guid? TenantId,
    string Name,
    string Url,
    IReadOnlyList<string> EventTypes,
    short MaxRetries,
    string RetryBackoff,
    short TimeoutSeconds,
    bool IsActive,
    short ConsecutiveFailures,
    DateTime? LastSuccessAt,
    DateTime? LastFailureAt,
    DateTime? AutoDisabledAt,
    DateTime CreatedAt,
    // Last-7d delivery success rate (delivered / total). NULL when there were no deliveries in the window
    // (divide-by-zero guarded) so the UI can render "—" rather than a misleading 0%.
    double? DeliverySuccessRate7d = null);

/// <summary>
/// Create a webhook subscription. The signing <see cref="Secret"/> is provided by the caller (or generated
/// if null) and returned ONCE via <see cref="CreateWebhookResult"/>; only its hash persists.
/// </summary>
public sealed record CreateWebhookRequest(
    Guid ClientId,
    Guid? TenantId,
    string Name,
    string Url,
    IReadOnlyList<string> EventTypes,
    string? Secret = null,
    short MaxRetries = 5,
    short TimeoutSeconds = 30);

public sealed record CreateWebhookResult(Guid WebhookId, string SigningSecret);

/// <summary>Update mutable webhook fields (URL/events/active).</summary>
public sealed record UpdateWebhookRequest(
    string? Name,
    string? Url,
    IReadOnlyList<string>? EventTypes,
    bool? IsActive);

/// <summary>A single delivery attempt record (maps to <c>platform_api.webhook_deliveries</c>).</summary>
public sealed record WebhookDeliveryDto(
    Guid DeliveryId,
    Guid WebhookId,
    string EventType,
    Guid EventId,
    string Status,             // pending|processing|success|failed|abandoned
    short AttemptCount,
    int? ResponseStatusCode,
    int? ResponseTimeMs,
    string? ErrorMessage,
    DateTime? NextRetryAt,
    DateTime CreatedAt,
    DateTime? DeliveredAt);

/// <summary>One subscribable event type (maps to <c>platform_api.api_event_types</c>).</summary>
public sealed record EventTypeDto(
    string EventType,
    string Resource,
    string Action,
    string Description,
    string? RequiresScope,
    bool IsActive);
