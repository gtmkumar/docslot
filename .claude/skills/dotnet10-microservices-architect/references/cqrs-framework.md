# Custom CQRS Framework (No MediatR)

Lives in `BuildingBlocks.Cqrs`. The whole point is a transparent, debuggable pipeline you own — no
reflection-heavy third-party mediator, no `IRequest<>` marker soup you can't step into. Handlers are
resolved from DI by closed generic interface; cross-cutting behaviors wrap the handler call.

## Contents
- [Core contracts](#core-contracts)
- [Dispatchers](#dispatchers)
- [Pipeline behaviors](#pipeline-behaviors)
- [DI registration](#di-registration)
- [Writing a command/query](#writing-a-commandquery)
- [Why this design](#why-this-design)

## Core contracts

```csharp
// BuildingBlocks.Cqrs/Abstractions/ICommand.cs
namespace BuildingBlocks.Cqrs;

// Marker for commands that return a value.
public interface ICommand<TResponse> { }

// Marker for commands with no meaningful return (use Unit, not void, to keep generic plumbing uniform).
public interface ICommand : ICommand<Unit> { }

public interface IQuery<TResponse> { }
```

```csharp
// BuildingBlocks.Cqrs/Abstractions/Handlers.cs
namespace BuildingBlocks.Cqrs;

public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<TResponse> Handle(TCommand command, CancellationToken ct);
}

// Convenience for the Unit-returning variant.
public interface ICommandHandler<in TCommand> : ICommandHandler<TCommand, Unit>
    where TCommand : ICommand<Unit> { }

public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<TResponse> Handle(TQuery query, CancellationToken ct);
}
```

```csharp
// BuildingBlocks.Cqrs/Unit.cs — a void stand-in so command/query plumbing is one generic shape.
namespace BuildingBlocks.Cqrs;

public readonly record struct Unit
{
    public static readonly Unit Value = new();
}
```

## Dispatchers

The dispatcher closes the open generic handler type at runtime, resolves it from the scoped
`IServiceProvider`, then composes the registered behaviors around the handler call. No assembly scanning
at dispatch time — registration happens once at startup.

```csharp
// BuildingBlocks.Cqrs/Abstractions/IDispatchers.cs
namespace BuildingBlocks.Cqrs;

public interface ICommandDispatcher
{
    Task<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken ct = default);
}

public interface IQueryDispatcher
{
    Task<TResponse> Query<TResponse>(IQuery<TResponse> query, CancellationToken ct = default);
}
```

```csharp
// BuildingBlocks.Cqrs/Dispatchers/CommandDispatcher.cs
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Cqrs;

public sealed class CommandDispatcher(IServiceProvider provider) : ICommandDispatcher
{
    public Task<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Close ICommandHandler<TCommand, TResponse> over the runtime command type.
        var handlerType = typeof(ICommandHandler<,>)
            .MakeGenericType(command.GetType(), typeof(TResponse));
        dynamic handler = provider.GetRequiredService(handlerType);

        // Behaviors wrap the handler. Resolve them closed over the same generic args.
        var behaviorType = typeof(IPipelineBehavior<,>)
            .MakeGenericType(command.GetType(), typeof(TResponse));
        var behaviors = ((IEnumerable<object>)provider.GetServices(behaviorType)).Reverse().ToArray();

        RequestHandlerDelegate<TResponse> next =
            () => handler.Handle((dynamic)command, ct);

        foreach (dynamic behavior in behaviors)
        {
            var current = next;
            next = () => behavior.Handle((dynamic)command, current, ct);
        }

        return next();
    }
}
```

```csharp
// BuildingBlocks.Cqrs/Dispatchers/QueryDispatcher.cs
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Cqrs;

public sealed class QueryDispatcher(IServiceProvider provider) : IQueryDispatcher
{
    public Task<TResponse> Query<TResponse>(IQuery<TResponse> query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var handlerType = typeof(IQueryHandler<,>)
            .MakeGenericType(query.GetType(), typeof(TResponse));
        dynamic handler = provider.GetRequiredService(handlerType);

        var behaviorType = typeof(IQueryPipelineBehavior<,>)
            .MakeGenericType(query.GetType(), typeof(TResponse));
        var behaviors = ((IEnumerable<object>)provider.GetServices(behaviorType)).Reverse().ToArray();

        RequestHandlerDelegate<TResponse> next = () => handler.Handle((dynamic)query, ct);
        foreach (dynamic behavior in behaviors)
        {
            var current = next;
            next = () => behavior.Handle((dynamic)query, current, ct);
        }
        return next();
    }
}
```

> **Note on `dynamic`:** it's used only to invoke the strongly-typed closed generic without writing a
> mountain of `MethodInfo.Invoke` reflection. The handler signatures are still fully typed and
> compile-checked at the handler definition site. If you want zero `dynamic`, cache compiled
> `Expression` delegates per request type in a `ConcurrentDictionary` — provided as an optional optimizer
> in the "Why this design" section.

## Pipeline behaviors

```csharp
// BuildingBlocks.Cqrs/Behaviors/IPipelineBehavior.cs
namespace BuildingBlocks.Cqrs;

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

public interface IPipelineBehavior<in TRequest, TResponse>
{
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct);
}

// Separate marker for queries so command-only behaviors (e.g. UoW commit) don't run on reads.
public interface IQueryPipelineBehavior<in TRequest, TResponse>
{
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct);
}
```

**Validation behavior** (FluentValidation, runs before the handler):

```csharp
// BuildingBlocks.Cqrs/Behaviors/ValidationBehavior.cs
using FluentValidation;

namespace BuildingBlocks.Cqrs;

public sealed class ValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators) : IPipelineBehavior<TRequest, TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (validators.Any())
        {
            var context = new ValidationContext<TRequest>(request);
            var failures = (await Task.WhenAll(
                    validators.Select(v => v.ValidateAsync(context, ct))))
                .SelectMany(r => r.Errors)
                .Where(f => f is not null)
                .ToArray();

            if (failures.Length != 0)
                throw new ValidationAppException(failures); // mapped to 400 by exception middleware
        }
        return await next();
    }
}
```

**Logging + timing behavior:**

```csharp
// BuildingBlocks.Cqrs/Behaviors/LoggingBehavior.cs
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Cqrs;

public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger) : IPipelineBehavior<TRequest, TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var name = typeof(TRequest).Name;
        var sw = Stopwatch.GetTimestamp();
        logger.LogInformation("Handling {Request}", name);
        try
        {
            var response = await next();
            logger.LogInformation("Handled {Request} in {Elapsed}ms",
                name, Stopwatch.GetElapsedTime(sw).TotalMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failure handling {Request}", name);
            throw;
        }
    }
}
```

**Unit-of-Work commit behavior** (commands only — wraps the handler so all writes commit atomically):

```csharp
// BuildingBlocks.Cqrs/Behaviors/UnitOfWorkBehavior.cs
namespace BuildingBlocks.Cqrs;

public sealed class UnitOfWorkBehavior<TRequest, TResponse>(IUnitOfWork uow)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : ICommand<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var response = await next();
        await uow.SaveChangesAsync(ct); // dispatches domain events + persists in one transaction
        return response;
    }
}
```

> `IUnitOfWork` is declared in `BuildingBlocks.Persistence`; `BuildingBlocks.Cqrs` references its
> abstraction only. Keep the interface in a tiny abstractions package if you want to avoid the
> Cqrs→Persistence reference — both are acceptable for building-blocks libs.

## DI registration

Register handlers by scanning assemblies once at startup. Behaviors register as open generics so they
apply to every request type.

```csharp
// BuildingBlocks.Cqrs/DependencyInjection.cs
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Cqrs;

public static class CqrsRegistration
{
    public static IServiceCollection AddCqrs(this IServiceCollection services, params Assembly[] assemblies)
    {
        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddScoped<IQueryDispatcher, QueryDispatcher>();

        foreach (var assembly in assemblies)
        {
            RegisterClosedImplementations(services, assembly, typeof(ICommandHandler<,>));
            RegisterClosedImplementations(services, assembly, typeof(IQueryHandler<,>));
        }

        // Behavior order = registration order. Logging → Validation → UnitOfWork → handler.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(UnitOfWorkBehavior<,>));
        services.AddScoped(typeof(IQueryPipelineBehavior<,>), typeof(LoggingBehavior<,>)); // reuse via adapter or duplicate type
        return services;
    }

    private static void RegisterClosedImplementations(
        IServiceCollection services, Assembly assembly, Type openInterface)
    {
        var implementations = assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == openInterface)
                .Select(i => (Service: i, Implementation: t)));

        foreach (var (service, implementation) in implementations)
            services.AddScoped(service, implementation);
    }
}
```

> The `UnitOfWorkBehavior` is constrained to `ICommand<TResponse>`; when the open-generic behavior is
> closed over a query type that doesn't satisfy the constraint, register it only against the command
> pipeline. Simplest robust approach: keep command behaviors on `IPipelineBehavior<,>` and query behaviors
> on `IQueryPipelineBehavior<,>` as shown, so reads never trigger a commit.

## Writing a command/query

A complete vertical slice — this is the shape every feature folder follows:

```csharp
// Ordering.Application/Commands/PlaceOrder/PlaceOrderCommand.cs
public sealed record PlaceOrderCommand(Guid CustomerId, IReadOnlyList<OrderLineDto> Lines)
    : ICommand<Guid>;   // returns the new order id

// PlaceOrderValidator.cs
public sealed class PlaceOrderValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l =>
            l.RuleFor(i => i.Quantity).GreaterThan(0));
    }
}

// PlaceOrderCommandHandler.cs
public sealed class PlaceOrderCommandHandler(IOrderRepository orders)
    : ICommandHandler<PlaceOrderCommand, Guid>
{
    public async Task<Guid> Handle(PlaceOrderCommand command, CancellationToken ct)
    {
        var order = Order.Place(command.CustomerId, command.Lines.Select(l => l.ToValueObject()));
        await orders.AddAsync(order, ct);
        // No SaveChanges here — UnitOfWorkBehavior commits and dispatches domain events.
        return order.Id;
    }
}
```

```csharp
// Ordering.Api/Controllers/OrdersController.cs — thin controller, no logic.
[ApiController]
[Route("api/orders")]
public sealed class OrdersController(ICommandDispatcher commands, IQueryDispatcher queries)
    : ControllerBase
{
    [HttpPost]
    [Authorize(Roles = "customer")]
    public async Task<IActionResult> Place(PlaceOrderCommand command, CancellationToken ct)
    {
        var id = await commands.Send(command, ct);
        return CreatedAtAction(nameof(GetById), new { id }, null);
    }

    [HttpGet("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
        => Ok(await queries.Query(new GetOrderByIdQuery(id), ct));
}
```

## Why this design

- **Owning the pipeline** means you can step into the dispatcher in a debugger, see exactly which
  behaviors run and in what order, and add a behavior (caching, idempotency, audit) without learning a
  library's extensibility model. MediatR's value is mostly the behaviors and assembly scanning — both
  of which are ~150 lines you now control.
- **Command/Query split at the dispatcher level** lets read-side skip the `UnitOfWork` commit entirely,
  which both avoids accidental writes during queries and saves a no-op `SaveChanges` round trip.
- **Optional perf optimization:** the `dynamic` calls in the dispatcher are JIT-cached but still slower
  than a direct call. For hot paths, cache a compiled delegate:

  ```csharp
  private static readonly ConcurrentDictionary<Type, Func<object, IServiceProvider, CancellationToken, Task>> _cache = new();
  ```

  Build the delegate once per request type via `Expression.Lambda`. In practice the `dynamic` path is
  fine for the vast majority of services — measure before optimizing.
