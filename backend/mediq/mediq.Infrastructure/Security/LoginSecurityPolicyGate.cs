using System.Globalization;
using mediq.Application.Abstractions;
using mediq.Domain.Platform;
using mediq.SharedDataModel.Docslot.Security;
using mediq.Utilities.Exceptions;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Enforces the tenant security policy (issue #91) at login — the REAL block, applied after credentials are
/// verified but before any token/session is minted. Reuses <c>platform.ip_allowlist</c> and the RBAC engine;
/// resolves the caller's effective permissions ONCE (only when a hours/MFA tier actually needs them).
/// </summary>
public sealed class LoginSecurityPolicyGate(
    ITenantSecurityPolicyService policies,
    IIpAllowlistService ipAllowlist,
    IRbacQueryService rbac)
    : ILoginSecurityPolicyGate
{
    // IST is a fixed +05:30 offset (no DST) — safe to compute without a tz database.
    private static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330);

    public async Task EnforceAsync(User user, Guid? activeTenantId, string? ipAddress, DateTime nowUtc, CancellationToken ct)
    {
        // A platform-level actor (no active tenant) is not bound to any tenant's policy.
        if (activeTenantId is not { } tenantId) return;

        var policy = await policies.GetAsync(tenantId, ct);

        // (3) IP allow-list — fail-closed when enabled (an empty list blocks everyone).
        if (policy.IpAllowlistEnabled && !await ipAllowlist.IsIpAllowedAsync(tenantId, ipAddress, ct))
            throw new ForbiddenException("Sign-in blocked: your network address is not permitted for this organization.");

        // Resolve permissions once — needed for the login-hours doctor exemption and the owners/admins MFA tier.
        var needsPerms = policy.RestrictLoginHours
            || !string.Equals(policy.MfaPolicy, MfaPolicyTiers.Optional, StringComparison.Ordinal);
        var permissions = needsPerms
            ? await rbac.ResolvePermissionsAsync(user.UserId, tenantId, ct)
            : (IReadOnlySet<string>)new HashSet<string>();

        // (2) Login hours (IST) — doctors optionally exempt.
        if (policy.RestrictLoginHours)
        {
            var isDoctor = permissions.Contains(SecurityPolicyPermissions.DoctorSelfKey);
            var exempt = policy.DoctorsExemptFromHours && isDoctor;
            if (!exempt && !WithinLoginHours(nowUtc, policy.LoginHoursStart, policy.LoginHoursEnd))
                throw new ForbiddenException("Sign-in blocked: outside the permitted login hours for this organization.");
        }

        // (1) 2FA policy — the distinct 'mfa_enrollment_required' outcome (403) when the tier covers the user.
        if (IsCoveredByMfaTier(policy.MfaPolicy, permissions) && !user.MfaEnabled)
            throw new MfaEnrollmentRequiredException();
    }

    /// <summary>owners_admins → holds an owner/admin key; all → everyone; optional → no one.</summary>
    private static bool IsCoveredByMfaTier(string tier, IReadOnlySet<string> permissions) => tier switch
    {
        MfaPolicyTiers.All => true,
        MfaPolicyTiers.OwnersAdmins => SecurityPolicyPermissions.OwnerAdminKeys.Any(permissions.Contains),
        _ => false,
    };

    /// <summary>
    /// True when "now" (converted to IST) falls within [start,end]. Handles an overnight window
    /// (start &gt; end, e.g. 22:00–06:00). Unparseable bounds fail OPEN (never lock everyone out on a typo).
    /// </summary>
    internal static bool WithinLoginHours(DateTime nowUtc, string start, string end)
    {
        if (!TryParse(start, out var s) || !TryParse(end, out var e)) return true;

        var utc = nowUtc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc) : nowUtc.ToUniversalTime();
        var ist = TimeOnly.FromDateTime(utc.Add(IstOffset));

        return s <= e ? ist >= s && ist <= e : ist >= s || ist <= e;   // normal vs overnight window
    }

    private static bool TryParse(string hhmm, out TimeOnly t) =>
        TimeOnly.TryParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t);
}
