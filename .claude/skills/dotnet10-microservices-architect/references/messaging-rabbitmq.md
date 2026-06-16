# RabbitMQ — Integration Events, Pub/Sub, Retry & Outbox

Lives in `BuildingBlocks.Messaging`. Cross-service communication is **event-driven** via RabbitMQ using a
topic exchange. Integration events are the contract between services; they are *not* domain events.

## Contents
- [Contracts](#contracts)
- [Event bus over RabbitMQ](#event-bus-over-rabbitmq)
- [Publishing — the outbox pattern](#publishing--the-outbox-pattern)
- [Consuming — handlers + retry](#consuming--handlers--retry)
- [Domain → integration translation](#domain--integration-translation)
- [DI wiring](#di-wiring)

## Contracts

```csharp
// BuildingBlocks.Messaging/IntegrationEvent.cs
namespace BuildingBlocks.Messaging;

public abstract record IntegrationEvent
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public DateTime OccurredOnUtc { get; init; } = DateTime.UtcNow;
}

public interface IIntegrationEventHandler<in TEvent> where TEvent : IntegrationEvent
{
    Task Handle(TEvent @event, CancellationToken ct);
}

public interface IEventBus
{
    Task PublishAsync(IntegrationEvent @event, CancellationToken ct = default);
}
```

A concrete contract, defined in the *publishing* service's `IntegrationEvents` folder and shared with
consumers via a small contracts package (or duplicated deliberately to keep services decoupled):

```csharp
// Ordering.Application/IntegrationEvents/OrderPlacedIntegrationEvent.cs
public sealed record OrderPlacedIntegrationEvent(Guid OrderId, Guid CustomerId, decimal Total)
    : IntegrationEvent;
```

## Event bus over RabbitMQ

Single topic exchange `enterprise.events`; routing key = event type name. Each service binds a durable
queue per handled event type. Uses `RabbitMQ.Client` v7 async API.

```csharp
// BuildingBlocks.Messaging/RabbitMq/RabbitMqEventBus.cs
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Messaging;

public sealed class RabbitMqEventBus(
    IConnection connection, ILogger<RabbitMqEventBus> logger) : IEventBus, IAsyncDisposable
{
    public const string ExchangeName = "enterprise.events";
    private IChannel? _channel;

    private async Task<IChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true }) return _channel;
        _channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await _channel.ExchangeDeclareAsync(
            ExchangeName, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        return _channel;
    }

    public async Task PublishAsync(IntegrationEvent @event, CancellationToken ct = default)
    {
        var channel = await EnsureChannelAsync(ct);
        var routingKey = @event.GetType().Name;
        var body = JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType());

        var props = new BasicProperties
        {
            Persistent = true,                       // survive broker restart
            MessageId = @event.Id.ToString(),
            Type = routingKey,
            ContentType = "application/json",
        };

        await channel.BasicPublishAsync(
            ExchangeName, routingKey, mandatory: false, basicProperties: props, body: body,
            cancellationToken: ct);

        logger.LogInformation("Published {Event} {Id}", routingKey, @event.Id);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
    }
}
```

## Publishing — the outbox pattern

Publishing directly inside a request risks losing the event if the broker is down after the DB commit,
or double-publishing on retry. Use a transactional **outbox**: the handler writes the integration event
to an `outbox_messages` table in the *same* transaction as the business change; a background dispatcher
relays unsent rows to RabbitMQ and marks them processed.

```csharp
// BuildingBlocks.Messaging/Outbox/OutboxMessage.cs
public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Type { get; set; }          // event type name
    public required string Content { get; set; }        // JSON payload
    public DateTime OccurredOnUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedOnUtc { get; set; }
    public string? Error { get; set; }
}
```

```csharp
// BuildingBlocks.Messaging/Outbox/OutboxProcessor.cs — BackgroundService relaying the outbox.
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Polly.Retry;

public sealed class OutboxProcessor(IServiceScopeFactory scopeFactory) : BackgroundService
{
    private static readonly ResiliencePipeline _retry = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 5,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromMilliseconds(500),
            UseJitter = true,
        })
        .Build();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();

            var pending = await db.GetUnprocessedAsync(batchSize: 50, stoppingToken);
            foreach (var message in pending)
            {
                try
                {
                    var type = OutboxTypeResolver.Resolve(message.Type);
                    var @event = (IntegrationEvent)JsonSerializer.Deserialize(message.Content, type)!;
                    await _retry.ExecuteAsync(async token => await bus.PublishAsync(@event, token), stoppingToken);
                    await db.MarkProcessedAsync(message.Id, stoppingToken);
                }
                catch (Exception ex)
                {
                    await db.MarkFailedAsync(message.Id, ex.Message, stoppingToken);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
```

## Consuming — handlers + retry

A hosted consumer subscribes the service's queue, deserializes by `Type` header, resolves the matching
`IIntegrationEventHandler<>`, and acks only on success. Failures are retried via DLX (dead-letter
exchange) with a delay, then parked.

```csharp
// BuildingBlocks.Messaging/RabbitMq/RabbitMqConsumer.cs
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

public sealed class RabbitMqConsumer(
    IConnection connection, IServiceScopeFactory scopeFactory,
    SubscriptionRegistry registry, ILogger<RabbitMqConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.ExchangeDeclareAsync(
            RabbitMqEventBus.ExchangeName, ExchangeType.Topic, durable: true, cancellationToken: ct);

        // Main queue + dead-letter for retry parking.
        var queue = registry.QueueName;
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = $"{RabbitMqEventBus.ExchangeName}.dlx"
            }, cancellationToken: ct);

        foreach (var routingKey in registry.RoutingKeys)
            await channel.QueueBindAsync(queue, RabbitMqEventBus.ExchangeName, routingKey, cancellationToken: ct);

        await channel.BasicQosAsync(0, prefetchCount: 10, global: false, ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            var typeName = ea.BasicProperties.Type ?? ea.RoutingKey;
            try
            {
                var json = Encoding.UTF8.GetString(ea.Body.Span);
                var eventType = registry.ResolveType(typeName);
                var @event = (IntegrationEvent)JsonSerializer.Deserialize(json, eventType)!;

                using var scope = scopeFactory.CreateScope();
                var handlerType = typeof(IIntegrationEventHandler<>).MakeGenericType(eventType);
                dynamic handler = scope.ServiceProvider.GetRequiredService(handlerType);
                await handler.Handle((dynamic)@event, ct);

                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed handling {Type}; dead-lettering", typeName);
                // requeue:false → routed to DLX for delayed retry, not infinite loop.
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
            }
        };

        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);
        await Task.Delay(Timeout.Infinite, ct);
    }
}
```

A consumer-side handler:

```csharp
// Catalog.Application/IntegrationEvents/OrderPlacedIntegrationEventHandler.cs
public sealed class OrderPlacedIntegrationEventHandler(IInventoryService inventory)
    : IIntegrationEventHandler<OrderPlacedIntegrationEvent>
{
    public async Task Handle(OrderPlacedIntegrationEvent @event, CancellationToken ct)
        => await inventory.ReserveForOrderAsync(@event.OrderId, ct);  // idempotent by OrderId
}
```

> **Idempotency is mandatory.** At-least-once delivery means handlers will occasionally re-run. Key every
> side effect on the event's `Id` or a natural business key, and no-op on duplicates.

## Domain → integration translation

A domain event handler (in-process, fired by `SaveChangesAsync`) translates to an integration event by
writing it to the outbox. This is the only correct seam between the two worlds.

```csharp
// Ordering.Application/Events/OrderPlacedDomainEventHandler.cs
public sealed class OrderPlacedDomainEventHandler(IOutboxStore outbox)
    : IDomainEventHandler<OrderPlacedDomainEvent>
{
    public async Task Handle(OrderPlacedDomainEvent e, CancellationToken ct)
        => await outbox.AddAsync(new OrderPlacedIntegrationEvent(e.OrderId, e.CustomerId, e.Total), ct);
}
```

## DI wiring

```csharp
// BuildingBlocks.Messaging/DependencyInjection.cs
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

public static class MessagingRegistration
{
    public static IServiceCollection AddRabbitMqMessaging(
        this IServiceCollection services, string connectionName, Action<SubscriptionRegistry> subscribe)
    {
        // Aspire provides the IConnection via AddRabbitMQClient(connectionName); else register a factory.
        services.AddSingleton<IEventBus, RabbitMqEventBus>();

        var registry = new SubscriptionRegistry();
        subscribe(registry);
        services.AddSingleton(registry);

        services.AddHostedService<RabbitMqConsumer>();
        services.AddHostedService<OutboxProcessor>();
        return services;
    }
}
```

```csharp
// In Catalog.Api/Program.cs
builder.Services.AddRabbitMqMessaging("rabbitmq", registry =>
{
    registry.QueueName = "catalog-service";
    registry.Subscribe<OrderPlacedIntegrationEvent, OrderPlacedIntegrationEventHandler>();
});
```

> `connectionName` ("rabbitmq") maps to the Aspire RabbitMQ resource — Aspire injects the broker
> connection. See `references/aspire-apphost.md`.
