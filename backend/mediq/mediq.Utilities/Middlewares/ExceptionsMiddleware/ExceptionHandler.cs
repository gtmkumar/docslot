using System.Net;
using System.Text.Json;
using mediq.Utilities.ApiResponse.ResponseUtil;
using mediq.Utilities.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace mediq.Utilities.Middlewares.ExceptionsMiddleware;

public class ExceptionHandler
{
    private const string UnauthorizedMarker = "UnAuthorized";

    // PostgreSQL SQLSTATE codes we translate into clean, non-leaky client messages.
    private const string SqlStateUniqueViolation     = "23505"; // duplicate key
    private const string SqlStateCheckViolation       = "23514"; // CHECK constraint
    private const string SqlStateInvalidTextRepr      = "22P02"; // invalid input syntax (e.g. bad enum/uuid)

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandler> _logger;

    public ExceptionHandler(RequestDelegate next, ILogger<ExceptionHandler> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleAsync(context, ex);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception ex)
    {
        var mapped = Classify(ex);

        // Always log the full exception (including any raw Npgsql/DB text) server-side.
        // The client body NEVER contains the inner DB message.
        _logger.LogError(
            ex,
            "Unhandled exception for {Method} {Path}",
            context.Request.Method,
            context.Request.Path.Value);

        if (context.Response.HasStarted)
        {
            // Response already flushed; nothing safe we can write.
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = (int)mapped.Status;
        context.Response.ContentType = "application/json";

        var payload = new Response
        {
            Status = false,
            Message = new Message
            {
                ErrorTypeCode   = mapped.Code,
                ErrorMessage    = mapped.Errors,
                ResponseMessage = mapped.ClientMessage
            }
        };

        await JsonSerializer.SerializeAsync(context.Response.Body, payload, JsonOptions);
    }

    private readonly record struct MappedError(
        ErrorMessageEnum Code,
        HttpStatusCode Status,
        string ClientMessage,
        IReadOnlyDictionary<string, string[]>? Errors);

    private static MappedError Classify(Exception ex)
    {
        switch (ex)
        {
            case ValidationException validation:
                return new MappedError(
                    ErrorMessageEnum.ValidationFailed, HttpStatusCode.UnprocessableEntity,
                    validation.Message, validation.ErrorsDictionary);

            case BusinessRuleException:
                return new MappedError(
                    ErrorMessageEnum.ValidationFailed, HttpStatusCode.UnprocessableEntity,
                    ex.Message, SingleError(ex, ex.Message));

            case ForbiddenException:
                return new MappedError(
                    ErrorMessageEnum.Forbidden, HttpStatusCode.Forbidden,
                    ex.Message, SingleError(ex, ex.Message));

            case ConflictException:
                return new MappedError(
                    ErrorMessageEnum.Conflict, HttpStatusCode.Conflict,
                    ex.Message, SingleError(ex, ex.Message));

            // DEF-4c: unknown lookup keys (e.g. POST /garments with an unknown tagCode)
            // surface as KeyNotFoundException → map to a clean 404 instead of a 500.
            case KeyNotFoundException:
                return new MappedError(
                    ErrorMessageEnum.NotFound, HttpStatusCode.NotFound,
                    "The requested resource was not found.",
                    SingleError(ex, "The requested resource was not found."));

            // DEF-4d: malformed request bodies (wrong JSON shape, e.g. an object where an
            // array is expected) bubble up as BadHttpRequestException and would otherwise
            // leak internal type names. Return a clean 400 envelope.
            case BadHttpRequestException:
                return new MappedError(
                    ErrorMessageEnum.BadRequest, HttpStatusCode.BadRequest,
                    "Malformed request body.",
                    SingleError(ex, "Malformed request body."));

            // DEF-4a: never leak raw Npgsql text. Translate known SQLSTATEs into safe
            // messages; everything else becomes a generic 400.
            case DbUpdateException dbEx:
                return ClassifyDbUpdate(dbEx);

            case UnauthorizedAccessException:
                return new MappedError(
                    ErrorMessageEnum.UnAuthorized, HttpStatusCode.Unauthorized,
                    "Unauthorized.", SingleError(ex, "Unauthorized."));

            default:
                // A sentinel-marked exception (thrown with UnauthorizedMarker as its Message) maps to 401;
                // anything else is a generic 500. Kept as an `if` inside default — NOT a `case not null when`
                // guard — so the switch does not widen ex's null-state and trip CS8604 (ex is non-null by
                // contract: both Classify's parameter and the catch in HandleAsync are non-nullable Exception).
                if (string.Equals(ex.Message, UnauthorizedMarker, StringComparison.Ordinal))
                    return new MappedError(
                        ErrorMessageEnum.UnAuthorized, HttpStatusCode.Unauthorized,
                        "Unauthorized.", SingleError(ex, "Unauthorized."));
                return new MappedError(
                    ErrorMessageEnum.Error, HttpStatusCode.InternalServerError,
                    "An unexpected error occurred.",
                    SingleError(ex, "An unexpected error occurred."));
        }
    }

    private static MappedError ClassifyDbUpdate(DbUpdateException dbEx)
    {
        var sqlState = ExtractSqlState(dbEx);

        return sqlState switch
        {
            SqlStateUniqueViolation => Make(
                ErrorMessageEnum.Conflict, HttpStatusCode.Conflict,
                "A record with the same value already exists."),
            SqlStateCheckViolation => Make(
                ErrorMessageEnum.ValidationFailed, HttpStatusCode.UnprocessableEntity,
                "Value violates a data constraint."),
            SqlStateInvalidTextRepr => Make(
                ErrorMessageEnum.ValidationFailed, HttpStatusCode.UnprocessableEntity,
                "Invalid value format."),
            _ => Make(
                ErrorMessageEnum.BadRequest, HttpStatusCode.BadRequest,
                "The request could not be saved due to a data error."),
        };

        static MappedError Make(ErrorMessageEnum code, HttpStatusCode status, string msg)
            => new(code, status, msg, new Dictionary<string, string[]> { ["error"] = new[] { msg } });
    }

    /// <summary>
    /// Pulls the PostgreSQL SQLSTATE from the inner exception without taking a hard
    /// dependency on the Npgsql package. Npgsql's PostgresException exposes a public
    /// string <c>SqlState</c> property; we read it by name so Utilities stays
    /// provider-agnostic and never surfaces the raw message to the client.
    /// </summary>
    private static string? ExtractSqlState(Exception ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            var prop = inner.GetType().GetProperty("SqlState");
            if (prop?.GetValue(inner) is string state && !string.IsNullOrEmpty(state))
                return state;
        }
        return null;
    }

    private static IReadOnlyDictionary<string, string[]> SingleError(Exception ex, string message)
        => new Dictionary<string, string[]> { [ex.GetType().Name] = new[] { message } };
}
