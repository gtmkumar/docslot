namespace mediq.Api.Middleware;

/// <summary>
/// Honors an inbound <c>X-Correlation-ID</c> (set by the YARP gateway) or generates one, stashes it in
/// <c>HttpContext.Items</c> for the request context + audit writer, echoes it on the response, and pushes
/// it into the Serilog scope so every log line in the request carries it. This is the seam through which
/// correlation flows HTTP → (later) RabbitMQ.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string Header = "X-Correlation-ID";

    public async Task Invoke(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(Header, out var existing) && !string.IsNullOrWhiteSpace(existing)
            ? existing.ToString()
            : Guid.CreateVersion7().ToString();

        context.Items["CorrelationId"] = correlationId;
        context.Response.Headers[Header] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
            await next(context);
    }
}

public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
        => app.UseMiddleware<CorrelationIdMiddleware>();
}
