namespace mediq.Domain.PlatformApi;

/// <summary>
/// A third-party application registered with the platform (maps to <c>platform_api.api_clients</c>).
/// Authenticates via OAuth 2.0 client-credentials. The secret is stored ONLY as a bcrypt/argon2 hash —
/// the plaintext is shown to the owner exactly once at registration / rotation.
/// </summary>
public sealed class ApiClient
{
    public Guid ClientId { get; private set; }
    public string ClientCode { get; private set; } = default!;
    public string ClientName { get; private set; } = default!;
    public string ClientSecretHash { get; private set; } = default!;
    public string ClientType { get; private set; } = default!;     // 'first_party' | 'partner' | 'public'

    public Guid? OwnerTenantId { get; private set; }
    public string OwnerEmail { get; private set; } = default!;
    public string? OwnerOrganization { get; private set; }

    public int RateLimitPerMinute { get; private set; }
    public int RateLimitPerDay { get; private set; }
    public int BurstLimit { get; private set; }

    public string? WebhookSigningSecret { get; private set; }

    public bool IsActive { get; private set; }
    public bool IsVerified { get; private set; }
    public DateTime? VerifiedAt { get; private set; }
    public Guid? VerifiedBy { get; private set; }

    public string Purpose { get; private set; } = default!;

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? LastUsedAt { get; private set; }
    public DateTime? DeletedAt { get; private set; }

    private ApiClient() { }

    /// <summary>
    /// True when the client is allowed to obtain tokens: active, not soft-deleted. Verification
    /// (manual approval) is required for partner/public clients — enforced by <see cref="CanIssueToken"/>.
    /// </summary>
    public bool CanIssueToken =>
        IsActive && DeletedAt is null && (ClientType == "first_party" || IsVerified);
}
