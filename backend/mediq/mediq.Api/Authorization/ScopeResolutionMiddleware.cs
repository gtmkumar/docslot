using System.Security.Claims;
using mediq.Application.Abstractions;
using mediq.Infrastructure.Security;

namespace mediq.Api.Authorization;

/// <summary>
/// For client-credentials requests (JWT carries <c>token_use=client</c>), resolves the token's scopes ONCE
/// per request into <see cref="IScopeContext"/> and confirms the token is still live (not revoked/expired)
/// against <c>platform_api.api_tokens</c> — so a revoked token fails closed even before its JWT expiry.
/// The scope set is taken from the DB row (authoritative) rather than trusting the claim alone.
/// </summary>
public sealed class ScopeResolutionMiddleware(RequestDelegate next)
{
    public async Task Invoke(
        HttpContext context,
        IScopeContext scopeContext,
        IApiTokenStore tokenStore,
        ITokenService tokenService)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true
            && user.FindFirst(JwtTokenService.TokenUseClaim)?.Value == JwtTokenService.TokenUseClient)
        {
            var bearer = ExtractBearer(context);
            if (bearer is not null)
            {
                var hash = tokenService.HashToken(bearer);
                var live = await tokenStore.FindLiveByHashAsync(hash, context.RequestAborted);
                if (live is not null)
                    scopeContext.Set(live.ClientId, live.GrantedScopes);
                // If null (revoked/expired/unknown), scopeContext stays empty → every RequireScope fails closed.
            }
        }

        await next(context);
    }

    private static string? ExtractBearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }
}

public static class ScopeResolutionMiddlewareExtensions
{
    public static IApplicationBuilder UseScopeResolution(this IApplicationBuilder app)
        => app.UseMiddleware<ScopeResolutionMiddleware>();
}
