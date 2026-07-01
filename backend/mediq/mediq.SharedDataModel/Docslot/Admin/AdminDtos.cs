namespace mediq.SharedDataModel.Docslot.Admin;

/// <summary>Tenant summary for the platform admin list/get surface (maps to <c>platform.tenants</c>).</summary>
public sealed record TenantDto(
    Guid TenantId, string TenantCode, string DisplayName, string TenantType,
    string PrimaryEmail, string Status, string Country, string? City);

/// <summary>A role a user holds in the tenant (for the user-row role chips). Carries the assignment id so
/// the manage panel can revoke without a second lookup.</summary>
public sealed record UserRoleDto(
    Guid UserTenantRoleId, Guid RoleId, string RoleKey, string Name, bool IsPrimary, DateTime? ExpiresAt);

/// <summary>User summary within a tenant. <c>IsActive</c> reflects TENANT activity (the user has at least one
/// active membership here), not the global <c>platform.users.is_active</c> flag. PHI: the phone is MASKED
/// server-side (raw phone never crosses the wire in an aggregate). <c>LockedUntil</c>/<c>MustChangePassword</c>
/// surface the account's security posture in the manage panel.</summary>
public sealed record UserListItemDto(
    Guid UserId, string Email, string FullName, string? MaskedPhone, bool IsActive, bool MfaEnabled,
    DateTime? LastLoginAt, DateTime? LockedUntil, bool MustChangePassword,
    IReadOnlyList<UserRoleDto> Roles);

/// <summary>Create-user request. A password is deliberately NOT accepted from the admin (impersonation
/// hazard) — the invite seeds a server-generated temp credential + must-change-password.</summary>
public sealed record CreateUserRequest(
    string Email, string FullName, string? Phone,
    string PreferredLanguage = "en", Guid? InitialRoleId = null);

/// <summary><c>AlreadyExisted</c>=true when the email matched an existing global identity and we only linked a
/// new tenant membership (never overwriting the existing user's profile).</summary>
public sealed record CreateUserResult(Guid UserId, bool AlreadyExisted = false);

/// <summary>Deactivate (revoke the user's memberships in this tenant) or reactivate (restore them). A reason is
/// mandatory when deactivating. Tenant-scoped — never flips the global users.is_active.</summary>
public sealed record SetUserStatusRequest(bool IsActive, string Reason);

public sealed record SetUserStatusResult(Guid UserId, bool IsActive);

/// <summary>Edit a user's profile (whitelisted fields only). Email/auth/status are never mutable here.</summary>
public sealed record UpdateUserProfileRequest(string FullName, string? Phone, string PreferredLanguage = "en");

public sealed record UpdateUserProfileResult(Guid UserId);

/// <summary>Force a password change + clear the lockout (flags only; no plaintext). A reason is mandatory.</summary>
public sealed record ResetAccessRequest(string Reason);

public sealed record ResetAccessResult(Guid UserId);

/// <summary>Role summary (maps to <c>platform.roles</c>). <c>MemberCount</c> = distinct users holding this role
/// with an ACTIVE assignment (revoked_at IS NULL AND not expired) in the resolved tenant scope. For a system
/// (cross-tenant) role it counts only members within the current tenant, consistent with how the list is scoped.</summary>
public sealed record RoleDto(Guid RoleId, string RoleKey, string Name, string Scope, bool IsSystem, Guid? TenantId,
    int MemberCount = 0);

/// <summary>Create a custom (tenant-scoped) role. System roles are seeded in SQL and cannot be created here.</summary>
public sealed record CreateRoleRequest(
    string RoleKey, string Name, string? Description, Guid? TenantId, string Scope = "tenant");

public sealed record CreateRoleResult(Guid RoleId);

/// <summary>Assign a role to a user within a tenant, optionally time-boxed.</summary>
public sealed record AssignRoleRequest(Guid UserId, Guid RoleId, Guid? TenantId, DateTime? ExpiresAt, bool IsPrimary = false);

public sealed record AssignRoleResult(Guid UserTenantRoleId);

/// <summary>Revoke an existing role assignment (soft — sets revoked_at/by/reason; never deletes the row).</summary>
public sealed record RevokeRoleRequest(Guid UserTenantRoleId, string Reason);

public sealed record RevokeRoleResult(Guid UserTenantRoleId, bool AlreadyRevoked);

/// <summary>Grant or deny a single permission to a user (override). Reason is mandatory; deny wins.</summary>
public sealed record SetOverrideRequest(
    Guid UserId, string PermissionKey, bool IsAllowed, string Reason, Guid? TenantId, DateTime? ExpiresAt);

public sealed record SetOverrideResult(Guid OverrideId);
