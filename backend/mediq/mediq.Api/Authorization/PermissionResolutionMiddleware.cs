using mediq.Application.Abstractions;
using mediq.Infrastructure.Security;

namespace mediq.Api.Authorization;

/// <summary>
/// Resolves the authenticated USER's effective permission set EXACTLY ONCE per request (NFR-PERF-01) via
/// <c>platform.resolve_user_permissions()</c> and caches it on the request-scoped
/// <see cref="IPermissionContext"/>. Every downstream <c>RequirePermission</c> check reads that cache in
/// memory. Client-credentials tokens (<c>token_use=client</c>) are SKIPPED — they carry scopes, not user
/// permissions, and are resolved by <see cref="ScopeResolutionMiddleware"/> instead.
/// </summary>
public sealed class PermissionResolutionMiddleware(RequestDelegate next)
{
    public async Task Invoke(
        HttpContext context,
        ICurrentUserContext currentUser,
        IPermissionContext permissions,
        IRbacQueryService rbac)
    {
        var isClientToken =
            context.User.FindFirst(JwtTokenService.TokenUseClaim)?.Value == JwtTokenService.TokenUseClient;

        if (!isClientToken
            && currentUser is { IsAuthenticated: true, UserId: { } userId }
            && !permissions.IsResolved)
        {
            var keys = await rbac.ResolvePermissionsAsync(userId, currentUser.TenantId, context.RequestAborted);
            permissions.Set(keys);
        }

        await next(context);
    }
}

public static class PermissionResolutionMiddlewareExtensions
{
    public static IApplicationBuilder UsePermissionResolution(this IApplicationBuilder app)
        => app.UseMiddleware<PermissionResolutionMiddleware>();
}
