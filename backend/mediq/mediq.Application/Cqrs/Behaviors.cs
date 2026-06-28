using System.Diagnostics;
using System.Text.Json;
using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Utilities.Exceptions;
using Microsoft.Extensions.Logging;

namespace mediq.Application.Cqrs;

/// <summary>
/// Translates Domain rule-violation exceptions into the shared <see cref="BusinessRuleException"/> (→ 422)
/// at the Application boundary, so Domain stays free of any Utilities/web dependency while clients still get
/// a clean 422 instead of a 500. Runs on commands (where domain mutations happen).
/// </summary>
public sealed class DomainExceptionTranslationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        try
        {
            return await next();
        }
        catch (mediq.Domain.Docslot.InvalidBookingTransitionException ex)
        {
            throw new BusinessRuleException(ex.Message);
        }
        catch (mediq.Domain.Docslot.SlotUnavailableException ex)
        {
            throw new BusinessRuleException(ex.Message);
        }
        catch (mediq.Domain.Commission.AttributionOnDiscountedBookingException ex)
        {
            // Discount ↔ attribution mutual exclusivity (DB trigger) → clean 422.
            throw new BusinessRuleException(ex.Message);
        }
        catch (mediq.Domain.Commission.PndtComplianceException ex)
        {
            throw new BusinessRuleException(ex.Message);
        }
    }
}

/// <summary>
/// Sets the per-request tenant scope (<c>app.tenant_id</c>) for RLS on READ paths inside a TRANSACTION that
/// is rolled back when the read completes — so the LOCAL GUC auto-clears and can't bleed onto a pooled
/// connection reused by another request (the prior session-scoped GUC was a cross-tenant RLS hazard).
/// Query handlers run their reads within this transaction. Pairs with the command-side UnitOfWorkBehavior.
/// </summary>
public sealed class TenantScopeQueryBehavior<TRequest, TResponse>(IUnitOfWork uow, ICurrentUserContext currentUser)
    : IQueryPipelineBehavior<TRequest, TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        await using var scope = await uow.BeginTenantScopeAsync(currentUser.TenantId, ct);
        return await next();   // disposing the scope rolls the read tx back → SET LOCAL is discarded
    }
}

/// <summary>Logging + timing. Runs on both commands and queries.</summary>
public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>, IQueryPipelineBehavior<TRequest, TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
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
            logger.LogWarning(ex, "Failure handling {Request} after {Elapsed}ms",
                name, Stopwatch.GetElapsedTime(sw).TotalMilliseconds);
            throw;
        }
    }
}

/// <summary>FluentValidation gate. Commands only (queries are read-shaped, validated at the edge).</summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var validatorList = validators as IValidator<TRequest>[] ?? validators.ToArray();
        if (validatorList.Length != 0)
        {
            var context = new ValidationContext<TRequest>(request);
            var results = await Task.WhenAll(validatorList.Select(v => v.ValidateAsync(context, ct)));
            var failures = results.SelectMany(r => r.Errors).Where(f => f is not null).ToArray();
            if (failures.Length != 0)
            {
                var errors = failures
                    .GroupBy(f => f.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).ToArray());
                // Reuse the Utilities ValidationException (DRY) → ExceptionHandler maps it to 422 with the errors dictionary.
                throw new mediq.Utilities.Exceptions.ValidationException(errors);
            }
        }
        return await next();
    }
}

/// <summary>
/// Idempotency-Key guard. Commands only. If the current request carries an Idempotency-Key already
/// processed for this (tenant, endpoint), returns the DURABLY cached response without re-executing the
/// handler (and flags the replay on the context so callers can surface <c>WasReplayed</c>); otherwise runs
/// the handler and persists the response. Now backed by a durable, table-backed store (slice 03 requirement)
/// so a retry can't double-confirm/double-create across restart or scale-out.
/// </summary>
public sealed class IdempotencyBehavior<TRequest, TResponse>(
    IIdempotencyContext idempotency,
    IIdempotencyStore store,
    ICurrentUserContext currentUser)
    : IPipelineBehavior<TRequest, TResponse>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var key = idempotency.Key;
        if (string.IsNullOrWhiteSpace(key))
        {
            // Commands marked as requiring idempotency (booking/money mutations) MUST carry a key.
            if (request is IRequireIdempotency)
                throw new mediq.Utilities.Exceptions.ValidationException(
                    new Dictionary<string, string[]> { ["Idempotency-Key"] = ["This operation requires an Idempotency-Key header."] });
            return await next();   // not an idempotency-guarded request
        }

        var cached = await store.TryGetAsync(currentUser.TenantId, idempotency.Endpoint, key, ct);
        if (cached is not null)
        {
            if (idempotency is IIdempotencyReplayMarker marker)
                marker.MarkReplayed();
            return JsonSerializer.Deserialize<TResponse>(cached, JsonOptions)!;
        }

        var response = await next();
        await store.SaveAsync(currentUser.TenantId, idempotency.Endpoint, key, JsonSerializer.Serialize(response, JsonOptions), ct);
        return response;
    }
}

/// <summary>Lets the idempotency behavior flag a replay so the API can set <c>WasReplayed=true</c> on the result.</summary>
public interface IIdempotencyReplayMarker
{
    bool WasReplayed { get; }
    void MarkReplayed();
}

/// <summary>Marker for commands that MUST carry an Idempotency-Key (booking/money mutations) — enforced by the behavior.</summary>
public interface IRequireIdempotency;

/// <summary>
/// Marker for commands that manage their OWN transaction boundaries — the <see cref="UnitOfWorkBehavior{TRequest,TResponse}"/>
/// must NOT wrap them in a single ambient transaction. Used when the handler performs an EXTERNAL side effect that
/// must happen OUTSIDE any open DB transaction (e.g. a payout gateway disbursement): wrapping it would hold a row
/// lock across network I/O AND let a crash after the external call roll the durable state back, risking a re-run that
/// double-applies the side effect. Such handlers open their own <see cref="IUnitOfWork.BeginTenantScopeAsync"/> scopes
/// (one committed phase before the external call, one after) so each phase is durable on its own.
/// </summary>
public interface ISelfManagedTransaction;

/// <summary>
/// Unit-of-Work commit + tenant scoping. Commands only. Sets <c>app.tenant_id</c> for RLS, runs the
/// handler, then commits once so all writes land atomically. Commands marked <see cref="ISelfManagedTransaction"/>
/// opt OUT of the single wrapping transaction and own their commit boundaries (see the interface docs).
/// </summary>
public sealed class UnitOfWorkBehavior<TRequest, TResponse>(
    IUnitOfWork uow, ICurrentUserContext currentUser)
    : IPipelineBehavior<TRequest, TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        // A self-managed command opens (and commits) its own tenant scopes — most importantly so an external
        // side effect (the payout gateway call) runs with NO DB transaction open and no row lock held across it.
        if (request is ISelfManagedTransaction)
            return await next();

        // Open a tenant-scoped transaction (SET LOCAL app.tenant_id), run the handler, persist, then commit.
        // The scope's transaction wraps the handler's writes so RLS applies and the GUC clears on commit.
        await using var scope = await uow.BeginTenantScopeAsync(currentUser.TenantId, ct);
        var response = await next();
        await uow.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return response;
    }
}
