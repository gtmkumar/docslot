using System.Diagnostics;
using mediq.Application.Abstractions;
using mediq.Infrastructure.Security;

namespace mediq.Api.Middleware;

/// <summary>
/// Logs EVERY client-credentials API request to <c>platform_api.api_requests</c> (method, path, status,
/// latency, client, tenant) for analytics + abuse detection, and enforces per-client rate limiting from
/// <c>api_clients.rate_limit_per_minute</c> BEFORE the request runs (429 + Retry-After on breach). Only
/// applies when the request carries a client token (<c>token_use=client</c>); user/anonymous traffic is
/// unaffected. Runs after scope resolution so the client id is known.
/// </summary>
public sealed class ApiClientRequestLogMiddleware(RequestDelegate next)
{
    public async Task Invoke(
        HttpContext context,
        IScopeContext scopes,
        IApiClientRepository clients,
        IApiRequestLogWriter requestLog)
    {
        // Not a client-token request → passthrough (this concern is platform_api-only).
        if (!scopes.IsClientToken || scopes.ClientId is not { } clientId)
        {
            await next(context);
            return;
        }

        // Per-client minute window rate limit (defense in depth; the gateway also limits at the edge).
        var client = await clients.GetByIdAsync(clientId, context.RequestAborted);
        if (client is not null)
        {
            var recent = await requestLog.CountRecentAsync(clientId, TimeSpan.FromMinutes(1), context.RequestAborted);
            if (recent >= client.RateLimitPerMinute)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = "60";
                await requestLog.RecordAsync(new ApiRequestLogEntry(
                    clientId, null, client.OwnerTenantId,
                    context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress?.ToString(),
                    context.Request.Headers.UserAgent.ToString(), StatusCodes.Status429TooManyRequests, 0, "rate_limited"),
                    context.RequestAborted);
                return;
            }
        }

        var sw = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var elapsed = (int)Stopwatch.GetElapsedTime(sw).TotalMilliseconds;
            await requestLog.RecordAsync(new ApiRequestLogEntry(
                clientId, null, client?.OwnerTenantId,
                context.Request.Method, context.Request.Path,
                context.Connection.RemoteIpAddress?.ToString(), context.Request.Headers.UserAgent.ToString(),
                context.Response.StatusCode, elapsed,
                context.Response.StatusCode >= 400 ? "error" : null),
                CancellationToken.None);
        }
    }
}

public static class ApiClientRequestLogMiddlewareExtensions
{
    public static IApplicationBuilder UseApiClientRequestLog(this IApplicationBuilder app)
        => app.UseMiddleware<ApiClientRequestLogMiddleware>();
}
