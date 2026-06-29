using mediq.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace mediq.Infrastructure.Messaging;

/// <summary>
/// The dev/test default integration-event bus (Messaging:Provider=none): an honest no-op. The durable outbox
/// still CAPTURES every event atomically with the business write; this just declines to ship it onward (the
/// broker + consumer are deferred). It performs NO I/O and never throws, so when the drain worker is enabled
/// without a configured broker a 'pending' row is simply marked 'success' (drained, not delivered) — never a
/// fake delivery, never a crash. Logs at debug only.
/// </summary>
public sealed class NullIntegrationEventBus(ILogger<NullIntegrationEventBus> logger) : IIntegrationEventBus
{
    public Task PublishAsync(
        Guid eventId, string eventType, Guid? tenantId, string payloadJson,
        string? correlationId, DateTime occurredAtUtc, CancellationToken ct)
    {
        logger.LogDebug(
            "NullIntegrationEventBus: dropping integration event {EventType} ({EventId}) — no broker configured (Messaging:Provider=none).",
            eventType, eventId);
        return Task.CompletedTask;
    }
}
