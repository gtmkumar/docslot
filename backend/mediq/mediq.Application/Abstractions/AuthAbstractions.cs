using mediq.Domain.Platform;

namespace mediq.Application.Abstractions;

/// <summary>Issues access JWTs and opaque refresh tokens (security-jwt patterns).</summary>
public interface ITokenService
{
    /// <summary>
    /// Short-lived signed access token. Carries <c>sub</c> (user id), <c>email</c>, <c>jti</c>, active tenant,
    /// and — when the user is a broker — a server-resolved <c>broker_id</c> claim. The broker self-service
    /// endpoints trust ONLY this claim for the caller's own broker identity (IDOR-safe), never a query param.
    /// </summary>
    AccessToken CreateAccessToken(User user, Guid? activeTenantId, Guid? brokerId = null);

    /// <summary>
    /// Short-lived OAuth client-credentials access token. Carries <c>client_id</c>, a space-delimited
    /// <c>scope</c> claim, the tenant, and <c>token_use=client</c> so the API can tell it apart from a user
    /// token (scopes vs. permissions). Reuses the same signing key/issuer/audience (DRY).
    /// </summary>
    AccessToken CreateClientAccessToken(Guid clientId, Guid? tenantId, IReadOnlyCollection<string> grantedScopes);

    /// <summary>Opaque random refresh token. Returns raw (to client) + SHA-256 hash (to store).</summary>
    (string Raw, string Hash) CreateRefreshToken();

    /// <summary>SHA-256 hex of a raw token value — used to match the stored hash without keeping plaintext.</summary>
    string HashToken(string raw);
}

public sealed record AccessToken(string Value, DateTime ExpiresAtUtc, int ExpiresInSeconds);

/// <summary>
/// Verifies a presented password against the stored hash. Supports BOTH bcrypt (pgcrypto <c>crypt()</c>
/// seeds, <c>$2a$/$2b$/$2y$</c>) and argon2id (security-hardening layer) by sniffing the hash prefix.
/// </summary>
public interface IPasswordHasher
{
    bool Verify(string password, string storedHash);

    /// <summary>Produces a new argon2id hash for newly-set passwords (preferred going forward).</summary>
    string Hash(string password);
}

/// <summary>Persists/rotates/revokes <c>platform.user_sessions</c> rows (token + refresh hashes).</summary>
public interface ISessionStore
{
    Task<Guid> CreateAsync(SessionCreate request, CancellationToken ct);
    Task<UserSessionRecord?> FindByRefreshHashAsync(string refreshTokenHash, CancellationToken ct);

    /// <summary>
    /// Finds a session by its refresh hash regardless of revoked state (for reuse-after-revoke detection).
    /// </summary>
    Task<UserSessionRecord?> FindByRefreshHashIncludingRevokedAsync(string refreshTokenHash, CancellationToken ct);

    Task RotateRefreshAsync(Guid sessionId, string newAccessHash, string newRefreshHash,
        DateTime newExpiresUtc, DateTime newRefreshExpiresUtc, CancellationToken ct);

    /// <summary>Rotates AND rebinds the session's active tenant (used by the secure switch-tenant flow).</summary>
    Task RotateRefreshWithTenantAsync(Guid sessionId, Guid? newActiveTenantId, string newAccessHash,
        string newRefreshHash, DateTime newExpiresUtc, DateTime newRefreshExpiresUtc, CancellationToken ct);

    Task RevokeAsync(Guid sessionId, string reason, CancellationToken ct);
    Task RevokeByAccessHashAsync(string accessTokenHash, string reason, CancellationToken ct);

    /// <summary>Revokes EVERY active session for a user — fail-closed response to refresh-token theft.</summary>
    Task RevokeAllForUserAsync(Guid userId, string reason, CancellationToken ct);
}

public sealed record SessionCreate(
    Guid UserId,
    string AccessTokenHash,
    string RefreshTokenHash,
    Guid? ActiveTenantId,
    string? DeviceInfo,
    string? IpAddress,
    DateTime ExpiresAtUtc,
    DateTime RefreshExpiresAtUtc);

public sealed record UserSessionRecord(
    Guid SessionId,
    Guid UserId,
    Guid? ActiveTenantId,
    DateTime RefreshExpiresAtUtc,
    DateTime? RevokedAtUtc);

/// <summary>Records login attempts and enforces the 5-failure lockout window against <c>platform.login_attempts</c> + <c>users.locked_until</c>.</summary>
public interface ILoginAttemptService
{
    Task RecordAsync(string email, string? ipAddress, string? userAgent, bool success, string? failureReason, CancellationToken ct);
}

/// <summary>
/// Resolves a platform user's OWN broker identity (<c>commission.brokers.user_id = userId</c>) at login, so
/// the broker_id claim is server-derived. Returns null for non-broker users. Keeps the Auth feature free of
/// a hard dependency on the whole commission repository.
/// </summary>
public interface IBrokerIdentityResolver
{
    Task<Guid?> ResolveBrokerIdAsync(Guid userId, CancellationToken ct);
}
