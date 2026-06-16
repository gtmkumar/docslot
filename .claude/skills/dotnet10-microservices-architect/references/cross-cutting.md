# Cross-Cutting Concerns — Exceptions, Serilog, Correlation ID, Audit

Lives in `BuildingBlocks.Web`. These middlewares and helpers are referenced by every `*.Api` and the
gateway so behavior is identical across the platform: one exception-to-HTTP mapping, one log shape, one
correlation header, one audit trail.

## Contents
- [Exception hierarchy](#exception-hierarchy)
- [Global exception handling → ProblemDetails](#global-exception-handling--problemdetails)
- [Correlation ID middleware](#correlation-id-middleware)
- [Serilog configuration](#serilog-configuration)
- [Audit logging](#audit-logging)

## Exception hierarchy

Domain and application exceptions carry enough information for the middleware to choose the right status
code without leaking internals.

```csharp
// BuildingBlocks.Domain/DomainException.cs
namespace BuildingBlocks.Domain;

public abstract class DomainException(string message) : Exception(message);

// e.g. Ordering.Domain/Exceptions/OrderNotFoundException.cs
public sealed class OrderNotFoundException(Guid id)
    : DomainException($"Order '{id}' was not found.");
```

```csharp
// BuildingBlocks.Cqrs/Exceptions/ValidationAppException.cs
using FluentValidation.Results;

public sealed class ValidationAppException(IEnumerable<ValidationFailure> failures) : Exception
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = failures
        .GroupBy(f => f.PropertyName)
        .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).ToArray());
}
```

## Global exception handling → ProblemDetails

Use .NET's `IExceptionHandler` (cleaner than try/catch middleware) and emit RFC 7807 `ProblemDetails` so
clients get a consistent error contract. Never return stack traces in production.

```csharp
// BuildingBlocks.Web/Exceptions/GlobalExceptionHandler.cs
using BuildingBlocks.Cqrs;
using BuildingBlocks.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        var correlationId = httpContext.Items["CorrelationId"]?.ToString();
        logger.LogError(exception, "Unhandled exception. CorrelationId={CorrelationId}", correlationId);

        var (status, title) = exception switch
        {
            ValidationAppException        => (StatusCodes.Status400BadRequest, "Validation failed"),
            OrderNotFoundException        => (StatusCodes.Status404NotFound, "Resource not found"),
            DomainException               => (StatusCodes.Status409Conflict, "Domain rule violated"),
            UnauthorizedAccessException   => (StatusCodes.Status403Forbidden, "Forbidden"),
            _                             => (StatusCodes.Status500InternalServerError, "Server error"),
        };

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status == StatusCodes.Status500InternalServerError
                ? "An unexpected error occurred."           // never leak internals on 500
                : exception.Message,
            Instance = httpContext.Request.Path,
        };
        problem.Extensions["correlationId"] = correlationId;

        if (exception is ValidationAppException vex)
            problem.Extensions["errors"] = vex.Errors;

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
```

```csharp
// BuildingBlocks.Web/Extensions/ExceptionExtensions.cs
public static class ExceptionExtensions
{
    public static IServiceCollection AddGlobalExceptionHandling(this IServiceCollection services)
    {
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();
        return services;
    }

    public static IApplicationBuilder UseExceptionHandling(this IApplicationBuilder app)
        => app.UseExceptionHandler();
}
```

## Correlation ID middleware

Every request gets a correlation ID — honored from the inbound `X-Correlation-ID` (set by the gateway) or
generated if absent. It's pushed into the logging scope so every log line in the request carries it, and
stashed in `HttpContext.Items` so the exception handler and YARP transform can read it.

```csharp
// BuildingBlocks.Web/Middleware/CorrelationIdMiddleware.cs
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    private const string Header = "X-Correlation-ID";

    public async Task Invoke(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(Header, out var existing)
            ? existing.ToString()
            : Guid.CreateVersion7().ToString();

        context.Items["CorrelationId"] = correlationId;
        context.Response.Headers[Header] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
            await next(context);
    }
}

public static class CorrelationIdExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
        => app.UseMiddleware<CorrelationIdMiddleware>();
}
```

## Serilog configuration

Structured logging to console (JSON in prod, pretty in dev) and enriched with correlation ID, trace ID,
and service name. Serilog's `OpenTelemetry` sink can forward to the same collector as traces/metrics so
logs correlate with spans.

```csharp
// In each service Program.cs — configure Serilog as the host logger.
using Serilog;

builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Service", context.HostingEnvironment.ApplicationName)
    .Enrich.WithSpan()                              // adds TraceId/SpanId (Serilog.Enrichers.Span)
    .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture)
    .WriteTo.OpenTelemetry());                      // forward logs to the OTLP collector
```

```jsonc
// appsettings.json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
      }
    }
  }
}
```

> Use `app.UseSerilogRequestLogging()` to collapse the noisy default per-request logs into one structured
> summary line per request (method, path, status, elapsed) — already carrying the correlation ID from the
> log scope.

## Audit logging

Two distinct things people conflate as "audit":

1. **Entity audit columns** (`CreatedBy`, `ModifiedAtUtc`, …) — handled automatically by the EF
   `AuditInterceptor`. See `references/data-efcore.md`.
2. **Audit trail of actions** (who did what, when, from where) — a domain concern when it must be
   queryable/immutable. Emit it as an explicit record, not just a log line, when the business needs
   tamper-evident history (e.g. financial/compliance contexts).

For an action audit trail, write through a small behavior or service that persists to an append-only
table (or ships to a dedicated audit store). Keep it separate from observability logs — Serilog logs are
for operators and rotate; the audit trail is a business record and is retained per policy.

```csharp
// BuildingBlocks.Web/Audit/IAuditTrail.cs
public interface IAuditTrail
{
    Task RecordAsync(AuditEntry entry, CancellationToken ct);
}

public sealed record AuditEntry(
    string Action,           // "PlaceOrder"
    string? UserId,
    string? CorrelationId,
    string? EntityType,
    string? EntityId,
    DateTime OccurredOnUtc);
```

A command behavior can record audit entries for write operations automatically (register it in the CQRS
pipeline like the others in `references/cqrs-framework.md`), keying off the command type name as the
action and pulling `UserId`/`CorrelationId` from `ICurrentUser` and `HttpContext`.
