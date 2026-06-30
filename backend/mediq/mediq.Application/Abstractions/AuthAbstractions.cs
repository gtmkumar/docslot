using mediq.Domain.Platform;

namespace mediq.Application.Abstractions;

/// <summary>
/// The sanctioned app-side gateway to the impersonation lifecycle (issue #3). Both methods invoke the
/// schema's SECURITY DEFINER functions (database/11_rbac_hardening.sql), which own the audit emission and
/// the permission/authorization guards — the C# layer never sets <c>ended_at</c> or the GUC directly. A
/// privilege failure (SQLSTATE 42501) surfaces as a 403.
/// </summary>
public interface IImpersonationRepository
{
    /// <summary>
    /// Opens an audited, time-boxed session via <c>platform.begin_impersonation()</c> and returns its id.
    /// <paramref name="actorUserId"/> MUST be the authenticated principal. The function re-checks
    /// <c>platform.users.impersonate</c> at the DB (defense in depth) and writes the hash-chained audit row.
    /// </summary>
    Task<Guid> BeginAsync(Guid actorUserId, Guid targetTenantId, string reason, Guid? targetUserId,
        TimeSpan ttl, bool breakGlass, CancellationToken ct);

    /// <summary>
    /// Closes a session via <c>platform.end_impersonation()</c>; returns false if it was already ended
    /// (idempotent). Enforces self-close-or-<c>platform.users.impersonate</c> and writes the close audit row.
    /// </summary>
    Task<bool> EndAsync(Guid impersonationId, Guid actorUserId, CancellationToken ct);
}

/// <summary>Issues access JWTs and opaque refresh tokens (security-jwt patterns).</summary>
public interface ITokenService
{
    /// <summary>
    /// Short-lived signed access token. Carries <c>sub</c> (user id), <c>email</c>, <c>jti</c>, active tenant,
    /// and — when the user is a broker — a server-resolved <c>broker_id</c> claim. The broker self-service
    /// endpoints trust ONLY this claim for the caller's own broker identity (IDOR-safe), never a query param.
    /// <para>
    /// <paramref name="impersonatedTenantId"/> (issue #3) is set ONLY by the begin-impersonation flow, after
    /// <c>platform.begin_impersonation()</c> has opened an audited, time-boxed session. It is server-signed and
    /// inert on its own: the DB guard <c>platform.current_impersonated_tenant()</c> ignores it unless a live
    /// session backs it, so a forged or stale claim unlocks no cross-tenant PHI.
    /// </para>
    /// </summary>
    AccessToken CreateAccessToken(User user, Guid? activeTenantId, Guid? brokerId = null, Guid? impersonatedTenantId = null);

    /// <summary>
    /// Short-lived OAuth client-credentials access token. Carries <c>client_id</c>, a space-delimited
    /// <c>scope</c> claim, the tenant, and <c>token_use=client</c> so the API can tell it apart from a user
    /// token (scopes vs. permissions). Reuses the same signing key/issuer/audience (DRY).
    /// </summary>
    AccessToken CreateClientAccessToken(Guid clientId, Guid? tenantId, IReadOnlyCollection<string> grantedScopes);

    /// <summary>
    /// A SHORT-LIVED, NON-HUMAN SERVICE access token for trusted in-process service-to-service calls (e.g. the
    /// no-show backfill worker calling the AI sibling without a live caller). Carries a fixed non-human
    /// <paramref name="subject"/> (NEVER a real user id), <c>token_use=service</c>, the target
    /// <paramref name="tenantId"/>, and a deliberately short <paramref name="ttlMinutes"/>. It is minted with the
    /// same signing key/issuer/audience the AI service already validates — so it needs NO external credential. The
    /// <c>token_use=service</c> claim lets the AI REFUSE it on every PHI path (it is accepted only on the non-PHI
    /// operational paths); it carries NO scopes/roles, so it confers no permissions beyond that.
    /// </summary>
    AccessToken CreateServiceToken(Guid tenantId, string subject, int ttlMinutes);

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
