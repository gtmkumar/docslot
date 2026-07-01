namespace mediq.SharedDataModel.Docslot.Security;

// Read + admin DTOs for active-session oversight (issue #87 — the People "Online" presence + admin revoke).
// SENSITIVE SURFACE: session token / refresh hashes are NEVER projected — only the metadata below.

/// <summary>
/// An ACTIVE session (not revoked, not expired) held by a MEMBER of the caller's tenant. Carries the owning
/// user's identity plus presence metadata. <see cref="IsSelf"/> flags a session belonging to the caller.
/// No token material of any kind is exposed.
/// </summary>
public sealed record ActiveSessionDto(
    Guid SessionId,
    Guid UserId,
    string UserName,
    string? UserEmail,
    string? IpAddress,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset ExpiresAt,
    bool IsSelf,
    string? City = null);          // geo-IP city (issue #94) — null offline (NullGeoIpResolver); UI then shows just the IP

/// <summary>Result of an admin sign-out-all for one user: how many active sessions were revoked.</summary>
public sealed record RevokeAllSessionsResult(Guid UserId, int RevokedCount);
