namespace mediq.Domain.PlatformApi;

/// <summary>Granular API permission, separate from RBAC (maps to <c>platform_api.api_scopes</c>).</summary>
public sealed class ApiScope
{
    public Guid ScopeId { get; private set; }
    public string ScopeKey { get; private set; } = default!;   // 'docslot.bookings.read'
    public string Resource { get; private set; } = default!;
    public string Action { get; private set; } = default!;
    public string Description { get; private set; } = default!;
    public bool IsDangerous { get; private set; }
    public bool RequiresConsent { get; private set; }

    private ApiScope() { }
}

/// <summary>The set of scopes a client may request (maps to <c>platform_api.api_client_scopes</c>).</summary>
public sealed class ApiClientScope
{
    public Guid ClientId { get; private set; }
    public Guid ScopeId { get; private set; }
    public DateTime GrantedAt { get; private set; }
    public Guid? GrantedBy { get; private set; }
    public DateTime? ExpiresAt { get; private set; }

    private ApiClientScope() { }
}

/// <summary>
/// An issued client-credentials JWT, tracked hashed for revocation (maps to <c>platform_api.api_tokens</c>).
/// Only the SHA-256 hash of the JWT is stored — never the token itself.
/// </summary>
public sealed class ApiToken
{
    public Guid TokenId { get; private set; }
    public Guid ClientId { get; private set; }
    public string TokenHash { get; private set; } = default!;
    public string[] RequestedScopes { get; private set; } = [];
    public string[] GrantedScopes { get; private set; } = [];
    public Guid? TenantId { get; private set; }
    public Guid? UserId { get; private set; }
    public DateTime IssuedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }

    private ApiToken() { }
}
