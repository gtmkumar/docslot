namespace mediq.Domain.Platform;

/// <summary>JWT/refresh-token session tracking for revocation (maps to <c>platform.user_sessions</c>).</summary>
public sealed class UserSession
{
    public Guid SessionId { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = default!;        // SHA-256 of access token
    public string? RefreshTokenHash { get; private set; }
    public Guid? ActiveTenantId { get; private set; }
    public string? DeviceInfo { get; private set; }
    public string? IpAddress { get; private set; }
    public DateTime IssuedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? RefreshExpiresAt { get; private set; }
    public DateTime LastActivityAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public string? RevokedReason { get; private set; }

    private UserSession() { }
}

/// <summary>Rate-limiting/lockout record (maps to <c>platform.login_attempts</c>).</summary>
public sealed class LoginAttempt
{
    public Guid AttemptId { get; private set; }
    public string Email { get; private set; } = default!;
    public string IpAddress { get; private set; } = default!;
    public string? UserAgent { get; private set; }
    public bool Success { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTime AttemptedAt { get; private set; }

    private LoginAttempt() { }
}
