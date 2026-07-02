namespace mediq.SharedDataModel.Docslot.Auth;

/// <summary>
/// Login request. Mirrors <c>POST /api/v1/auth/login</c>. The frontend sends email + password; an
/// optional <see cref="TenantId"/> selects the active tenant when the user belongs to several.
/// </summary>
public sealed record LoginRequest(string Email, string Password, Guid? TenantId = null, string? DeviceInfo = null);

/// <summary>
/// Token bundle returned by login/refresh. <see cref="AccessToken"/> is a short-lived JWT;
/// <see cref="RefreshToken"/> is an opaque rotating value (stored hashed server-side).
/// </summary>
public sealed record TokenResponse(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    Guid UserId,
    Guid? ActiveTenantId,
    bool MfaRequired);

/// <summary>Refresh request — rotates the refresh token and mints a new access token.</summary>
public sealed record RefreshRequest(string RefreshToken);

/// <summary>
/// Self-service password change (<c>POST /api/v1/auth/change-password</c>). The new password must satisfy the
/// tenant security policy's <c>minPasswordLength</c> (issue #91) — a shorter value is rejected with 422. The
/// caller is the authenticated principal; the current password is re-verified before the change is applied.
/// </summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>
/// Switch the active tenant for a user who belongs to several. Server-side membership is validated and a
/// NEW access token carrying the requested tenant claim is minted — the only secure way to change the
/// tenant that scopes RLS/PHI access. <see cref="RefreshToken"/> binds the switch to the live session.
/// </summary>
public sealed record SwitchTenantRequest(Guid TenantId, string RefreshToken);

/// <summary>Logout request — revokes the session bound to the presented access token.</summary>
public sealed record LogoutRequest(string? RefreshToken = null);

/// <summary>
/// Begin a support impersonation session (issue #3). Opens an AUDITED, time-boxed
/// <c>platform.begin_impersonation()</c> session for the authenticated actor and mints a NEW access token
/// carrying the server-signed <c>impersonated_tenant</c> claim. The actor is ALWAYS the authenticated
/// principal — never taken from the body. <see cref="RefreshToken"/> binds the operation to the live
/// session (rotated on success). <see cref="TargetUserId"/> narrows the session to one user (null =
/// tenant-wide support); <see cref="TtlMinutes"/> defaults server-side (30); <see cref="BreakGlass"/>
/// raises a critical alert + records a legal-obligation basis.
/// </summary>
public sealed record BeginImpersonationRequest(
    Guid TargetTenantId,
    string Reason,
    string RefreshToken,
    Guid? TargetUserId = null,
    int? TtlMinutes = null,
    bool BreakGlass = false);

/// <summary>
/// Token bundle + impersonation metadata returned by <c>begin</c>. The embedded <see cref="Token"/> carries
/// the <c>impersonated_tenant</c> claim; cross-tenant PHI still opens only because a live session backs it.
/// </summary>
public sealed record ImpersonationResponse(
    TokenResponse Token,
    Guid ImpersonationId,
    Guid TargetTenantId,
    DateTime ExpiresAtUtc);

/// <summary>
/// End an active impersonation session (issue #3) and re-mint a CLEAN (non-impersonating) access token so
/// the cleared scope takes effect immediately. <c>platform.end_impersonation()</c> enforces
/// self-close-or-<c>platform.users.impersonate</c> at the DB and writes the symmetric audit row.
/// </summary>
public sealed record EndImpersonationRequest(Guid ImpersonationId, string RefreshToken);

/// <summary>
/// Authenticated profile for <c>GET /api/v1/me</c>. <see cref="ActiveTenantId"/> is the tenant the
/// current token is scoped to; <see cref="Tenants"/> lists every tenant the user may switch into;
/// <see cref="Roles"/> carries the caller's role display labels IN the active tenant (display-only —
/// authorization always flows from the effective permission set, never from these labels).
/// </summary>
public sealed record MeDto(
    Guid UserId,
    string Email,
    string FullName,
    string PreferredLanguage,
    string Timezone,
    bool MfaEnabled,
    Guid? ActiveTenantId,
    IReadOnlyList<MeTenantDto> Tenants,
    IReadOnlyList<MeRoleDto> Roles);

public sealed record MeTenantDto(Guid TenantId, string TenantCode, string DisplayName, string TenantType, bool IsPrimary);

/// <summary>A role held by the caller in the active tenant — display label only, not an authz input.</summary>
public sealed record MeRoleDto(string RoleKey, string Name);

/// <summary>A single dashboard badge count keyed by <c>navigation_menus.badge_source</c>. Mirrors <c>GET /api/v1/me/badges</c>.</summary>
public sealed record BadgesDto(IReadOnlyDictionary<string, int> Counts);
