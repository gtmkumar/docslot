using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Primitives;

namespace mediq.Api.Caching;

/// <summary>Names + tags for the output-cache policies (server-side rendered-response caching for
/// reads whose JSON is identical across callers and changes rarely). One place so controllers and
/// Program.cs can't drift.</summary>
public static class OutputCachePolicies
{
    /// <summary>platform.permissions catalog (<c>GET /iam/permissions</c>): platform-wide, user/tenant-invariant;
    /// varies only by the <c>?module=</c> filter. Evicted by tag when the catalog changes.</summary>
    public const string IamPermissions = "iam-permissions";

    /// <summary>Module list (<c>GET /iam/modules</c>): the catalog rows are platform-wide but each row's
    /// <c>Licensed</c> flag is PER-TENANT, so the key varies by the signed tenant claims. Evicted by tag on
    /// module create AND license change.</summary>
    public const string IamModules = "iam-modules";

    /// <summary>Seeded platform_api registries (<c>GET /api-scopes</c>, <c>GET /webhooks/event-types</c>):
    /// identical for every caller, no write endpoint exists — TTL-only regeneration.</summary>
    public const string ApiCatalog = "api-catalog";

    /// <summary>PIN-code reference (<c>GET /geo/pincode/{pin}</c>): public postal geography, identical for
    /// every caller, effectively immutable — long TTL keyed by path.</summary>
    public const string GeoReference = "geo-reference";

    /// <summary>Eviction tag covering every IAM-catalog-derived cache entry.</summary>
    public const string IamCatalogTag = "iam-catalog";
}

/// <summary>
/// Output-cache gate for AUTHENTICATED endpoints. The framework's default policy refuses to cache any
/// request carrying an <c>Authorization</c> header — correct in general, but our candidates are catalog/
/// reference reads whose bodies are caller-invariant (or keyed by the varying claim), and the pipeline
/// places <c>UseOutputCache()</c> AFTER <c>UseAuthentication</c>/<c>UseAuthorization</c>, so JWT validation,
/// the resolve-once permission load, and every <c>[RequirePermission]</c> policy still run on CACHE HITS —
/// a caller who lost access gets 401/403 before the cache is ever consulted. What a hit skips is only MVC
/// dispatch, the handler's DB queries, and JSON serialization.
/// <para>
/// Mirrors the default policy's remaining safety rails: GET only, 200 only, never when the response sets a
/// cookie. Applied ONLY via the named policies above — nothing is cached unless an endpoint opts in.
/// </para>
/// </summary>
public sealed class AuthenticatedReferenceCachePolicy : IOutputCachePolicy
{
    ValueTask IOutputCachePolicy.CacheRequestAsync(OutputCacheContext context, CancellationToken ct)
    {
        var cacheable = HttpMethods.IsGet(context.HttpContext.Request.Method);
        context.EnableOutputCaching = true;
        context.AllowCacheLookup = cacheable;
        context.AllowCacheStorage = cacheable;
        context.AllowLocking = true;

        // Default key already includes scheme + host + path; query-string/claim variation is added per policy.
        return ValueTask.CompletedTask;
    }

    ValueTask IOutputCachePolicy.ServeFromCacheAsync(OutputCacheContext context, CancellationToken ct)
        => ValueTask.CompletedTask;

    ValueTask IOutputCachePolicy.ServeResponseAsync(OutputCacheContext context, CancellationToken ct)
    {
        var response = context.HttpContext.Response;
        if (response.StatusCode != StatusCodes.Status200OK ||
            !StringValues.IsNullOrEmpty(response.Headers.SetCookie))
        {
            context.AllowCacheStorage = false;
        }
        return ValueTask.CompletedTask;
    }
}
