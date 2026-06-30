using mediq.Api.Authorization;
using mediq.Api.Context;
using mediq.Application.Abstractions;
using mediq.Application.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace mediq.Api.Extensions;

public static class ApiServiceExtensions
{
    /// <summary>
    /// JWT bearer validation with the SAME parameters the gateway uses (drift here is a security hole).
    /// Validates issuer, audience, signing key, lifetime; 30s clock skew.
    /// </summary>
    // SECURITY: the committed dev signing key can no longer reach production — JwtSigningKeyGuard.Validate
    // (called at host startup in Program.cs) now ENFORCES this, failing closed (throws) outside Development
    // when the dev key or a sub-256-bit key is configured. Source a real key via Jwt__SigningKey.
    // SECURITY TODO (still future, external-credential-gated): replace symmetric HMAC-SHA256 with RS256/JWKS so
    // the signing key never leaves the Identity service, and source it from a real secret store. Audit condition.
    public static IServiceCollection AddPlatformJwtAuth(this IServiceCollection services, IConfiguration config)
    {
        var jwt = config.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? throw new InvalidOperationException("Missing 'Jwt' configuration section.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwt.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = System.Security.Claims.ClaimTypes.NameIdentifier,
                };
            });

        return services;
    }

    /// <summary>
    /// Authorization for BOTH schemes: dynamic <c>perm:&lt;key&gt;</c> (user permissions) and
    /// <c>scope:&lt;key&gt;</c> (client scopes) policies + their in-memory handlers.
    /// </summary>
    public static IServiceCollection AddPlatformAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, ScopeAuthorizationHandler>();
        services.AddAuthorization();
        return services;
    }

    /// <summary>Request-scoped context ports (current user/tenant, permission cache, scope cache, idempotency, clock).</summary>
    public static IServiceCollection AddRequestContext(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        // Request-scoped tenant override for the anonymous WhatsApp webhook (set server-side from the
        // phone_number_id map; consumed by CurrentUserContext only when there is no JWT tenant claim).
        services.AddScoped<ITenantScopeOverride, TenantScopeOverride>();
        services.AddScoped<IAmbientIdempotencyKey, AmbientIdempotencyKey>();
        services.AddScoped<ICurrentUserContext, CurrentUserContext>();
        services.AddScoped<IPermissionContext, PermissionContext>();
        services.AddScoped<IScopeContext, ScopeContext>();
        services.AddScoped<IIdempotencyContext, IdempotencyContext>();
        services.AddSingleton<IClock, SystemClock>();
        return services;
    }
}
