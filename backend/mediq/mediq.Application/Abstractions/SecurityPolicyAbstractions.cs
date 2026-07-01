using mediq.Domain.Platform;
using mediq.SharedDataModel.Docslot.Security;

namespace mediq.Application.Abstractions;

/// <summary>
/// The typed, effective tenant security policy (issue #91). This is the in-app representation of
/// <c>platform.tenants.settings-&gt;'security'</c>; <see cref="Default"/> supplies the fallbacks so a tenant that
/// never configured a policy still resolves a complete, non-blocking baseline (optional MFA, no login-hours
/// restriction, no IP allow-list). Times are IST "HH:mm" (matching the business-hours convention).
/// </summary>
public sealed record SecurityPolicy(
    string MfaPolicy,
    int MinPasswordLength,
    int IdleTimeoutMinutes,
    bool RequireNewDeviceVerification,
    bool RestrictLoginHours,
    string LoginHoursStart,
    string LoginHoursEnd,
    bool DoctorsExemptFromHours,
    bool IpAllowlistEnabled,
    bool MaskSensitiveForReceptionist)
{
    /// <summary>
    /// Sensible, NON-blocking defaults. <c>MaskSensitiveForReceptionist</c> defaults ON (privacy-preserving,
    /// matching the pre-existing always-masked behaviour); every gate (MFA/hours/IP) defaults OFF so an
    /// unconfigured tenant behaves exactly as before this feature shipped.
    /// </summary>
    public static SecurityPolicy Default { get; } = new(
        MfaPolicy: MfaPolicyTiers.Optional,
        MinPasswordLength: 8,
        IdleTimeoutMinutes: 30,
        RequireNewDeviceVerification: false,
        RestrictLoginHours: false,
        LoginHoursStart: "00:00",
        LoginHoursEnd: "23:59",
        DoctorsExemptFromHours: true,
        IpAllowlistEnabled: false,
        MaskSensitiveForReceptionist: true);

    public SecurityPolicyDto ToDto(int staffPendingMfaEnrolment) => new(
        MfaPolicy, MinPasswordLength, IdleTimeoutMinutes, RequireNewDeviceVerification,
        RestrictLoginHours, LoginHoursStart, LoginHoursEnd, DoctorsExemptFromHours,
        IpAllowlistEnabled, MaskSensitiveForReceptionist, staffPendingMfaEnrolment);
}

/// <summary>
/// Reads + persists the tenant security policy in the <c>platform.tenants.settings</c> JSONB (no new table),
/// and derives the "staff pending 2FA enrolment" count. platform.tenants carries no RLS, so every method
/// scopes strictly by the supplied <paramref name="tenantId"/> (which callers bind from the signed JWT).
/// </summary>
public interface ITenantSecurityPolicyService
{
    /// <summary>Effective policy for a tenant (stored keys merged over <see cref="SecurityPolicy.Default"/>).</summary>
    Task<SecurityPolicy> GetAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Overwrites <c>settings-&gt;'security'</c> with <paramref name="policy"/> (leaves other settings keys intact).</summary>
    Task SaveAsync(Guid tenantId, SecurityPolicy policy, CancellationToken ct);

    /// <summary>
    /// Count of active tenant members who would be forced to enrol on next login under <paramref name="policy"/>:
    /// members covered by the required-2FA tier whose <c>users.mfa_enabled = false</c>. 0 for the optional tier.
    /// Role-derived (ignores per-user overrides) — a dashboard estimate, not an authz decision.
    /// </summary>
    Task<int> CountStaffPendingMfaEnrolmentAsync(Guid tenantId, SecurityPolicy policy, CancellationToken ct);
}

/// <summary>
/// Manages <c>platform.ip_allowlist</c> CIDRs for a tenant and answers the login-time "is this source IP
/// allowed?" question. DELETE is a soft-deactivate (<c>is_active = false</c>) because <c>docslot_app</c> is
/// granted SELECT/INSERT/UPDATE only (no DELETE) on platform tables.
/// </summary>
public interface IIpAllowlistService
{
    Task<IReadOnlyList<mediq.SharedDataModel.Docslot.Security.IpAllowlistEntryDto>> ListAsync(Guid tenantId, CancellationToken ct);

    /// <summary>Adds an active tenant-wide CIDR entry; returns its id. <paramref name="createdByUserId"/> is audited on the row.</summary>
    Task<Guid> AddAsync(Guid tenantId, Guid createdByUserId, string cidrRange, string? label, DateTimeOffset? expiresAt, CancellationToken ct);

    /// <summary>Deactivates an entry (soft delete); false when it does not belong to the tenant or was already inactive.</summary>
    Task<bool> DeactivateAsync(Guid tenantId, Guid allowlistId, CancellationToken ct);

    /// <summary>
    /// True when <paramref name="ipAddress"/> falls inside ANY active, non-expired tenant-wide CIDR (Postgres
    /// <c>&gt;&gt;=</c> containment). A tenant with the toggle on but ZERO active entries is treated as "block all"
    /// (fail-closed) — enforcing an empty allow-list must never silently admit everyone.
    /// </summary>
    Task<bool> IsIpAllowedAsync(Guid tenantId, string? ipAddress, CancellationToken ct);
}

/// <summary>
/// The login-time security-policy gate (issue #91). Called by <see cref="Features.Auth.Login.LoginCommandHandler"/>
/// AFTER the password is verified and the active tenant is resolved, but BEFORE any token/session is issued —
/// so a policy violation withholds the session entirely. Throws:
/// <list type="bullet">
/// <item><see cref="mediq.Utilities.Exceptions.MfaEnrollmentRequiredException"/> (403, distinct code) when the
/// user's tier requires 2FA and the account has none.</item>
/// <item><see cref="mediq.Utilities.Exceptions.ForbiddenException"/> (403) for an out-of-hours or non-allow-listed
/// IP sign-in.</item>
/// </list>
/// No-op for a session with no active tenant (a platform-level actor is not bound to any tenant's policy).
/// </summary>
public interface ILoginSecurityPolicyGate
{
    Task EnforceAsync(User user, Guid? activeTenantId, string? ipAddress, DateTime nowUtc, CancellationToken ct);
}

/// <summary>
/// Permission keys that classify a user's tier for security-policy decisions. Kept here (not hardcoded role
/// names — the schema forbids role-name checks) so both the login gate and the pending-enrolment count agree.
/// </summary>
public static class SecurityPolicyPermissions
{
    /// <summary>Holding EITHER of these = "owner/admin" tier for the <c>owners_admins</c> MFA policy.</summary>
    public static readonly string[] OwnerAdminKeys = ["tenant.users.update", "tenant.roles.assign"];

    /// <summary>
    /// A doctor signals via a self-scoped permission the doctor role carries but front-desk (tenant_staff) does
    /// not. <c>docslot.doctor.update_self</c> is the concrete one that exists in the catalogue (there is no
    /// <c>read_self</c> key) and is held by the doctor role but not by tenant_staff.
    /// </summary>
    public const string DoctorSelfKey = "docslot.doctor.update_self";

    /// <summary>Clinical staff (hold medical-history read) are exempt from receptionist sensitive-field masking.</summary>
    public const string ClinicalReadKey = "docslot.medical_history.read";
}
