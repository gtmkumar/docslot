namespace mediq.Application.Cqrs;

/// <summary>
/// A void stand-in so command/query plumbing is one uniform generic shape
/// (handlers always return <c>Task&lt;T&gt;</c>, never <c>Task</c>).
/// </summary>
public readonly record struct Unit
{
    public static readonly Unit Value = new();
}

/// <summary>Marker for a command that returns a value.</summary>
public interface ICommand<TResponse>;

/// <summary>Marker for a command with no meaningful return (uses <see cref="Unit"/>).</summary>
public interface ICommand : ICommand<Unit>;

/// <summary>Marker for a query that returns a value.</summary>
public interface IQuery<TResponse>;

public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<TResponse> Handle(TCommand command, CancellationToken ct);
}

/// <summary>Convenience handler for the <see cref="Unit"/>-returning command variant.</summary>
public interface ICommandHandler<in TCommand> : ICommandHandler<TCommand, Unit>
    where TCommand : ICommand<Unit>;

public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<TResponse> Handle(TQuery query, CancellationToken ct);
}

/// <summary>The next link in the behavior chain (eventually the handler itself).</summary>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

/// <summary>Cross-cutting behavior that wraps command handling (validation, logging, audit, UoW, idempotency).</summary>
public interface IPipelineBehavior<in TRequest, TResponse>
{
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct);
}

/// <summary>
/// Separate marker for query behaviors so command-only behaviors (UoW commit, audit, idempotency)
/// never run on reads. Reads get logging only.
/// </summary>
public interface IQueryPipelineBehavior<in TRequest, TResponse>
{
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct);
}

public interface ICommandDispatcher
{
    Task<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken ct = default);
}

public interface IQueryDispatcher
{
    Task<TResponse> Query<TResponse>(IQuery<TResponse> query, CancellationToken ct = default);
}
