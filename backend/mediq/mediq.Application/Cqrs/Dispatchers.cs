using Microsoft.Extensions.DependencyInjection;

namespace mediq.Application.Cqrs;

/// <summary>
/// Closes <see cref="ICommandHandler{TCommand,TResponse}"/> over the runtime command type, resolves it
/// from the scoped <see cref="IServiceProvider"/>, then composes the registered command behaviors around
/// the handler call. No assembly scanning at dispatch time — registration happens once at startup.
/// </summary>
public sealed class CommandDispatcher(IServiceProvider provider) : ICommandDispatcher
{
    public Task<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResponse));
        dynamic handler = provider.GetRequiredService(handlerType);

        var behaviorType = typeof(IPipelineBehavior<,>).MakeGenericType(command.GetType(), typeof(TResponse));
        var behaviors = ((IEnumerable<object>)provider.GetServices(behaviorType)).Reverse().ToArray();

        RequestHandlerDelegate<TResponse> next = () => handler.Handle((dynamic)command, ct);
        foreach (dynamic behavior in behaviors)
        {
            var current = next;
            next = () => behavior.Handle((dynamic)command, current, ct);
        }
        return next();
    }
}

/// <summary>
/// Query counterpart of <see cref="CommandDispatcher"/>. Uses the separate
/// <see cref="IQueryPipelineBehavior{TRequest,TResponse}"/> chain so reads never trigger a UoW commit,
/// audit write, or idempotency capture.
/// </summary>
public sealed class QueryDispatcher(IServiceProvider provider) : IQueryDispatcher
{
    public Task<TResponse> Query<TResponse>(IQuery<TResponse> query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var handlerType = typeof(IQueryHandler<,>).MakeGenericType(query.GetType(), typeof(TResponse));
        dynamic handler = provider.GetRequiredService(handlerType);

        var behaviorType = typeof(IQueryPipelineBehavior<,>).MakeGenericType(query.GetType(), typeof(TResponse));
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
