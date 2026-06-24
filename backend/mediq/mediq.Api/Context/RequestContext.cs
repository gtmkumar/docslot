using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using mediq.Application.Abstractions;
using mediq.Infrastructure.Security;

namespace mediq.Api.Context;

/// <summary>
/// Request-scoped implementation of <see cref="ICurrentUserContext"/> over <see cref="IHttpContextAccessor"/>.
/// Reads the authenticated principal AND the active tenant EXCLUSIVELY from the validated JWT — never from a
/// client header.
/// <para>
/// SECURITY (auditor blocker, slice 01): the active tenant is the value PostgreSQL RLS uses to scope PHI
/// (<c>set_config('app.tenant_id', …)</c> in <c>UnitOfWorkBehavior</c>) and the value passed to
/// <c>resolve_user_permissions()</c>. It therefore MUST be the server-signed <c>tenant_id</c> claim and can
/// never be silently overridden by an <c>X-Tenant-Id</c> header. To switch tenant, a user calls
/// <c>POST /api/v1/auth/switch-tenant</c>, which validates membership server-side and mints a NEW token
/// carrying the new claim. No unvalidated header path exists.
/// </para>
/// </summary>
public sealed class CurrentUserContext(IHttpContextAccessor accessor, ITenantScopeOverride tenantOverride) : ICurrentUserContext
{
    private HttpContext? Http => accessor.HttpContext;
    private ClaimsPrincipal? User => Http?.User;

    public Guid? UserId =>
        Guid.TryParse(
            User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? User?.FindFirst("sub")?.Value, out var id)
            ? id : null;

    public string? Email =>
        User?.FindFirst(ClaimTypes.Email)?.Value ?? User?.FindFirst(JwtRegisteredClaimNames.Email)?.Value;

    /// <summary>
    /// The active tenant — the validated JWT <c>tenant_id</c> claim for authenticated requests. There is
    /// intentionally no <c>X-Tenant-Id</c> header fallback: a client cannot select a tenant it was not issued
    /// a token for. For ANONYMOUS, server-trusted entry points (the WhatsApp webhook) that have no JWT, the
    /// tenant is resolved server-side from the trusted <c>phone_number_id</c> map and supplied via
    /// <see cref="ITenantScopeOverride"/> — never from client input. The JWT claim always takes precedence.
    /// </summary>
    public Guid? TenantId =>
        Guid.TryParse(User?.FindFirst(JwtTokenService.TenantClaim)?.Value, out var fromToken)
            ? fromToken
            : tenantOverride.TenantId;

    public string? CorrelationId => Http?.Items["CorrelationId"]?.ToString();
    public string? IpAddress => Http?.Connection.RemoteIpAddress?.ToString();
    public string? UserAgent => Http?.Request.Headers.UserAgent.ToString();
    public bool IsAuthenticated => User?.Identity?.IsAuthenticated ?? false;

    /// <summary>The caller's own broker id from the signed <c>broker_id</c> claim (broker-role users only).</summary>
    public Guid? BrokerId =>
        Guid.TryParse(User?.FindFirst(JwtTokenService.BrokerClaim)?.Value, out var id) ? id : null;

    /// <summary>
    /// The tenant being impersonated, from the server-signed <c>impersonated_tenant</c> claim (issue #3). No
    /// header fallback — like <see cref="TenantId"/>, it must be a minted, signed claim. Inert unless a live
    /// <c>impersonation_sessions</c> row backs it at the DB layer, so it cannot be used to forge PHI access.
    /// </summary>
    public Guid? ImpersonatedTenantId =>
        Guid.TryParse(User?.FindFirst(JwtTokenService.ImpersonatedTenantClaim)?.Value, out var id) ? id : null;
}

/// <summary>
/// Request-scoped resolve-once permission cache (NFR-PERF-01). The authorization middleware populates it
/// exactly once per request; <see cref="IPermissionContext.Has"/> checks read this in memory.
/// </summary>
public sealed class PermissionContext : IPermissionContext
{
    private IReadOnlySet<string> _keys = new HashSet<string>(StringComparer.Ordinal);
    public bool IsResolved { get; private set; }
    public IReadOnlySet<string> Keys => _keys;

    public void Set(IReadOnlySet<string> keys)
    {
        _keys = keys;
        IsResolved = true;
    }

    public bool Has(string permissionKey) => _keys.Contains(permissionKey);
}

/// <summary>
/// Request-scoped Idempotency-Key holder, populated from the <c>Idempotency-Key</c> header. The
/// <see cref="Endpoint"/> discriminator (METHOD + path) keys the durable store so the same key on different
/// endpoints does not collide. Also surfaces a replay flag so a booking-action result can be re-stamped
/// with <c>WasReplayed=true</c>.
/// </summary>
public sealed class IdempotencyContext(IHttpContextAccessor accessor, IAmbientIdempotencyKey ambient)
    : IIdempotencyContext, mediq.Application.Cqrs.IIdempotencyReplayMarker
{
    /// <summary>
    /// The HTTP <c>Idempotency-Key</c> header when present; otherwise the server-supplied ambient key (set by
    /// internal dispatchers such as the WhatsApp inbound handler). The header always wins.
    /// </summary>
    public string? Key
    {
        get
        {
            if (accessor.HttpContext?.Request.Headers.TryGetValue("Idempotency-Key", out var v) == true
                && !string.IsNullOrWhiteSpace(v.ToString()))
                return v.ToString();
            return ambient.Key;
        }
    }

    public string Endpoint
    {
        get
        {
            var http = accessor.HttpContext;
            return http is null ? "unknown" : $"{http.Request.Method} {http.Request.Path}";
        }
    }

    public bool WasReplayed { get; private set; }
    public void MarkReplayed() => WasReplayed = true;
}

/// <summary>System clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
