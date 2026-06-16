namespace mediq.Domain.Platform;

/// <summary>
/// Platform identity (maps to <c>platform.users</c>). A user authenticates centrally here and is
/// authorized per-tenant via <c>user_tenant_roles</c>. Database-first: this entity carries only the
/// columns slice 01 (identity/auth) needs; the canonical schema is authoritative.
/// </summary>
public sealed class User
{
    public Guid UserId { get; private set; }
    public string Email { get; private set; } = default!;     // citext UNIQUE
    public string? Phone { get; private set; }
    public string? PasswordHash { get; private set; }          // bcrypt (pgcrypto seed) or argon2id; null for SSO-only
    public string FullName { get; private set; } = default!;

    public bool EmailVerified { get; private set; }
    public bool PhoneVerified { get; private set; }
    public bool MfaEnabled { get; private set; }
    public string? SsoProvider { get; private set; }

    public DateTime? LastLoginAt { get; private set; }
    public string? LastLoginIp { get; private set; }           // inet
    public short FailedLoginCount { get; private set; }
    public DateTime? LockedUntil { get; private set; }
    public bool MustChangePassword { get; private set; }

    public string PreferredLanguage { get; private set; } = "en";
    public string Timezone { get; private set; } = "Asia/Kolkata";

    public bool IsActive { get; private set; }
    public bool IsPlatformUser { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? DeletedAt { get; private set; }

    private User() { }

    /// <summary>True when the account is currently locked out (failed-login window not yet elapsed).</summary>
    public bool IsLockedOut(DateTime nowUtc) => LockedUntil is { } until && until > nowUtc;

    /// <summary>True when the account can authenticate at all (active, not soft-deleted, has a credential).</summary>
    public bool CanAuthenticate => IsActive && DeletedAt is null && PasswordHash is not null;

    // Mutators are applied via repository UPDATEs in Infrastructure; kept internal-friendly via setters
    // exposed only to the persistence layer in this slice (EF sets backing fields by configuration).
    public void StampSuccessfulLogin(DateTime nowUtc, string? ip)
    {
        LastLoginAt = nowUtc;
        LastLoginIp = ip;
        FailedLoginCount = 0;
        LockedUntil = null;
    }

    public void RegisterFailedLogin(DateTime nowUtc, int lockoutThreshold, TimeSpan lockoutDuration)
    {
        FailedLoginCount = (short)(FailedLoginCount + 1);
        if (FailedLoginCount >= lockoutThreshold)
            LockedUntil = nowUtc.Add(lockoutDuration);
    }
}
