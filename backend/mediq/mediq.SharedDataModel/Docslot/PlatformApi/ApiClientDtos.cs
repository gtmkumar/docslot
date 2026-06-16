namespace mediq.SharedDataModel.Docslot.PlatformApi;

/// <summary>API client summary (maps to <c>platform_api.api_clients</c>). NEVER carries the secret/hash.</summary>
public sealed record ApiClientDto(
    Guid ClientId,
    string ClientCode,
    string ClientName,
    string ClientType,
    Guid? OwnerTenantId,
    string OwnerEmail,
    string? OwnerOrganization,
    bool IsActive,
    bool IsVerified,
    int RateLimitPerMinute,
    int RateLimitPerDay,
    int BurstLimit,
    IReadOnlyList<string> GrantedScopes,
    DateTime CreatedAt,
    DateTime? LastUsedAt);

/// <summary>Register a new API client (manual-approval workflow — created inactive/unverified).</summary>
public sealed record RegisterApiClientRequest(
    string ClientCode,
    string ClientName,
    string ClientType,            // 'first_party' | 'partner' | 'public'
    string OwnerEmail,
    string? OwnerOrganization,
    Guid? OwnerTenantId,
    string Purpose);

/// <summary>
/// Result of registering or rotating a secret. The plaintext <see cref="ClientSecret"/> is returned ONCE
/// here and never stored (only its hash persists); the caller must capture it immediately.
/// </summary>
public sealed record ApiClientSecretResult(Guid ClientId, string ClientCode, string ClientSecret);

/// <summary>Grant/revoke scopes for a client (the requestable scope set).</summary>
public sealed record SetClientScopesRequest(IReadOnlyList<string> ScopeKeys);

/// <summary>Approve (verify+activate) or suspend a client.</summary>
public sealed record SetClientStatusRequest(bool IsActive, bool IsVerified, string? Reason = null);

/// <summary>Set per-client rate limits.</summary>
public sealed record SetClientRateLimitsRequest(int RateLimitPerMinute, int RateLimitPerDay, int BurstLimit);
