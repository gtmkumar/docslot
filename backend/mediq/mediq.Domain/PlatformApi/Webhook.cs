namespace mediq.Domain.PlatformApi;

/// <summary>Outbound event subscription (maps to <c>platform_api.webhook_subscriptions</c>).</summary>
public sealed class WebhookSubscription
{
    public Guid WebhookId { get; private set; }
    public Guid ClientId { get; private set; }
    public Guid? TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string Url { get; private set; } = default!;
    public string SecretHash { get; private set; } = default!;   // HMAC signing secret, hashed at rest
    public string[] EventTypes { get; private set; } = [];
    public short MaxRetries { get; private set; }
    public string RetryBackoff { get; private set; } = "exponential";
    public short TimeoutSeconds { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime? LastSuccessAt { get; private set; }
    public DateTime? LastFailureAt { get; private set; }
    public short ConsecutiveFailures { get; private set; }
    public DateTime? AutoDisabledAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private WebhookSubscription() { }

    public bool IsDeliverable => IsActive && AutoDisabledAt is null;
}

/// <summary>Registry of subscribable events (maps to <c>platform_api.api_event_types</c>).</summary>
public sealed class ApiEventType
{
    public string EventType { get; private set; } = default!;   // 'docslot.booking.created'
    public string Resource { get; private set; } = default!;
    public string Action { get; private set; } = default!;
    public string Description { get; private set; } = default!;
    public string? RequiresScope { get; private set; }
    public bool IsActive { get; private set; }

    private ApiEventType() { }
}

/// <summary>
/// A delivery attempt for a webhook (maps to <c>platform_api.webhook_deliveries</c>). Tracks status,
/// attempt count, HMAC signature sent, response, and the next retry time. <c>event_id</c> is the
/// idempotency key.
/// </summary>
public sealed class WebhookDelivery
{
    public Guid DeliveryId { get; private set; }
    public Guid WebhookId { get; private set; }
    public string EventType { get; private set; } = default!;
    public Guid EventId { get; private set; }
    public string Payload { get; private set; } = default!;   // JSONB serialized
    public string? Signature { get; private set; }
    public string Status { get; private set; } = "pending";   // pending|processing|success|failed|abandoned
    public short AttemptCount { get; private set; }
    public int? ResponseStatusCode { get; private set; }
    public int? ResponseTimeMs { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTime? NextRetryAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? DeliveredAt { get; private set; }

    private WebhookDelivery() { }
}
