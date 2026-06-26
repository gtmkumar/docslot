namespace mediq.SharedDataModel.Docslot.Admin;

/// <summary>Tenant summary for the platform admin list/get surface (maps to <c>platform.tenants</c>).</summary>
public sealed record TenantDto(
    Guid TenantId, string TenantCode, string DisplayName, string TenantType,
    string PrimaryEmail, string Status, string Country, string? City);

/// <summary>A role a user holds in the tenant (for the user-row role chips). Carries the assignment id so
/// the manage panel can revoke without a second lookup.</summary>
public sealed record UserRoleDto(
    Guid UserTenantRoleId, Guid RoleId, string RoleKey, string Name, bool IsPrimary, DateTime? ExpiresAt);

/// <summary>User summary within a tenant (maps to <c>platform.users</c>), with the roles they hold here.</summary>
public sealed record UserListItemDto(
    Guid UserId, string Email, string FullName, string? Phone, bool IsActive, bool MfaEnabled, DateTime? LastLoginAt,
    IReadOnlyList<UserRoleDto> Roles);

/// <summary>Create-user request. Password is optional (SSO-only users supply none).</summary>
public sealed record CreateUserRequest(
    string Email, string FullName, string? Phone, string? Password,
    string PreferredLanguage = "en", Guid? InitialRoleId = null);

public sealed record CreateUserResult(Guid UserId);

/// <summary>Role summary (maps to <c>platform.roles</c>).</summary>
public sealed record RoleDto(Guid RoleId, string RoleKey, string Name, string Scope, bool IsSystem, Guid? TenantId);

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
