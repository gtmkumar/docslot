namespace mediq.Application.Options;

/// <summary>JWT signing/validation parameters, sourced from configuration (never hardcoded except dev defaults).</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "docslot-platform";
    public string Audience { get; set; } = "docslot-clients";

    /// <summary>Base64-encoded HMAC-SHA256 signing key. Dev-only default; supply via secrets/Aspire in prod.</summary>
    public string SigningKey { get; set; } = default!;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
}

/// <summary>Authentication policy knobs (lockout threshold/window).</summary>
public sealed class AuthPolicyOptions
{
    public const string SectionName = "AuthPolicy";
    public int LockoutThreshold { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}

/// <summary>
/// Symmetric passphrase for at-rest field encryption (e.g. webhook signing secrets). Sourced from
/// configuration/secrets — never hardcoded except a dev default.
/// </summary>
public sealed class EncryptionOptions
{
    public const string SectionName = "Encryption";
    public string Passphrase { get; set; } = default!;
}
