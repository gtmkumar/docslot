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

        // Per-client rate limiting (defense in depth; the gateway also limits at the edge). Enforce BOTH the
        // trailing-minute and trailing-day windows (api_clients.rate_limit_per_minute / rate_limit_per_day),
        // counted in one query. NOTE: this is a sliding window over api_requests — correct + durable, but a
        // per-request COUNT; at scale the prod path is a distributed counter / the YARP edge limiter.
        var client = await clients.GetByIdAsync(clientId, context.RequestAborted);
        if (client is not null)
        {
            var now = DateTime.UtcNow;
            var (perMinute, perDay) = await requestLog.CountWindowsAsync(
                clientId, now.AddMinutes(-1), now.AddDays(-1), context.RequestAborted);

            // Minute window first (the tighter bound). Retry-After is an advisory hint: the minute bound clears
            // within 60s; the day bound is a "back off substantially" signal (a precise sliding reset would need
            // the oldest in-window timestamp).
            var breachedWindow =
                perMinute >= client.RateLimitPerMinute ? "60" :
                perDay >= client.RateLimitPerDay ? "3600" : null;
            if (breachedWindow is not null)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = breachedWindow;
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
