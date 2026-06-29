using System.Text;
using mediq.Application.Abstractions;
using mediq.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace mediq.Infrastructure.Messaging;

/// <summary>
/// The real (Messaging:Provider=rabbitmq) integration-event publisher. Declares a durable topic exchange
/// (<see cref="MessagingOptions.ExchangeName"/>) and publishes each event with the event_type as the routing
/// key and <c>x-event-id</c> / <c>x-correlation-id</c> / <c>x-tenant-id</c> headers, using publisher confirms
/// so a publish only resolves once the broker has accepted (durably) the message — the drain worker's
/// MarkPublished/MarkFailed transition is therefore broker-acknowledged, not fire-and-forget.
/// <para>
/// Constructed ONLY when Messaging:Provider=rabbitmq (see <c>InfrastructureRegistration</c>); the dev/test
/// default wires <see cref="NullIntegrationEventBus"/> and never touches RabbitMQ. The <see cref="IConnection"/>
/// is the Aspire-registered singleton (<c>AddRabbitMQClient</c>).
/// </para>
/// <para>
/// WIRED but UNVERIFIED against a live broker in this slice: no broker is stood up and no consumer exists yet
/// (both deferred). A channel is opened per publish (channels are not thread-safe; a fresh one is the safe,
/// honest choice until a verified pooled-channel design lands alongside a real broker + consumer).
/// </para>
/// </summary>
public sealed class RabbitMqIntegrationEventBus(
    IConnection connection,
    IOptions<MessagingOptions> options,
    ILogger<RabbitMqIntegrationEventBus> logger) : IIntegrationEventBus
{
    private readonly MessagingOptions _options = options.Value;

    public async Task PublishAsync(
        Guid eventId, string eventType, Guid? tenantId, string payloadJson,
        string? correlationId, DateTime occurredAtUtc, CancellationToken ct)
    {
        // Publisher confirms: BasicPublishAsync completes only once the broker has durably accepted the message.
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);

        // Idempotent exchange declaration (durable topic, survives broker restart). Cheap; the broker no-ops a
        // re-declare with identical args.
        await channel.ExchangeDeclareAsync(
            exchange: _options.ExchangeName,
            type: _options.ExchangeType,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: ct);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,   // persisted to disk → survives a broker restart
            MessageId = eventId.ToString(),
            CorrelationId = correlationId,
            Timestamp = new AmqpTimestamp(new DateTimeOffset(occurredAtUtc, TimeSpan.Zero).ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>
            {
                ["x-event-id"] = eventId.ToString(),
                ["x-event-type"] = eventType,
                ["x-correlation-id"] = correlationId,
                ["x-tenant-id"] = tenantId?.ToString(),
            },
        };

        var body = Encoding.UTF8.GetBytes(payloadJson);
        await channel.BasicPublishAsync(
            exchange: _options.ExchangeName,
            routingKey: eventType,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: ct);

        logger.LogDebug("Published integration event {EventType} ({EventId}) to exchange {Exchange}.",
            eventType, eventId, _options.ExchangeName);
    }
}
