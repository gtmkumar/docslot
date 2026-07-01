namespace mediq.SharedDataModel.Docslot.Security;

/// <summary>
/// The effective, tenant-scoped security policy (issue #91). Persisted under
/// <c>platform.tenants.settings-&gt;'security'</c> (no new table). Absent keys fall back to sensible
/// defaults, so a tenant that never configured a policy still returns a fully-populated object.
/// <para>
/// Every field here is REALLY enforced somewhere in the request path (see the enforcement points in the
/// slice notes): <see cref="MfaPolicy"/> + <see cref="MinPasswordLength"/> + <see cref="RestrictLoginHours"/>
/// + <see cref="IpAllowlistEnabled"/> gate the login / password-set flows; <see cref="MaskSensitiveForReceptionist"/>
/// gates the patient-detail read. A stored toggle that nothing checks would be a compliance failure.
/// </para>
/// </summary>
public sealed record SecurityPolicyDto(
    string MfaPolicy,
    int MinPasswordLength,
    int IdleTimeoutMinutes,
    bool RequireNewDeviceVerification,
    bool RestrictLoginHours,
    string LoginHoursStart,
    string LoginHoursEnd,
    bool DoctorsExemptFromHours,
    bool IpAllowlistEnabled,
    bool MaskSensitiveForReceptionist,
    /// <summary>Derived: how many active staff, subject to a REQUIRED-2FA tier, still lack <c>mfa_enabled</c>
    /// and would therefore be forced to enrol on their next login. 0 when <see cref="MfaPolicy"/> = optional.</summary>
    int StaffPendingMfaEnrolment);

/// <summary>Update payload for <c>PUT /api/v1/security/policy</c>. Same shape minus the derived count.</summary>
public sealed record UpdateSecurityPolicyRequest(
    string MfaPolicy,
    int MinPasswordLength,
    int IdleTimeoutMinutes,
    bool RequireNewDeviceVerification,
    bool RestrictLoginHours,
    string LoginHoursStart,
    string LoginHoursEnd,
    bool DoctorsExemptFromHours,
    bool IpAllowlistEnabled,
    bool MaskSensitiveForReceptionist);

/// <summary>Recognised MFA-policy tiers (mirrors the frontend contract). Anything else is rejected at update.</summary>
public static class MfaPolicyTiers
{
    public const string Optional = "optional";
    public const string OwnersAdmins = "owners_admins";
    public const string All = "all";

    public static readonly string[] All_ = [Optional, OwnersAdmins, All];
}

/// <summary>One row of <c>platform.ip_allowlist</c> (tenant-scoped). The raw CIDR is safe to surface — it is
/// network metadata, not a secret.</summary>
public sealed record IpAllowlistEntryDto(
    Guid AllowlistId,
    string CidrRange,
    string? Label,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>Add payload for <c>POST /api/v1/security/ip-allowlist</c>.</summary>
public sealed record AddIpAllowlistRequest(string CidrRange, string? Label, DateTimeOffset? ExpiresAt);
