using mediq.Domain.Platform;

namespace mediq.Application.Abstractions;

/// <summary>
/// Aggregate-style access to <c>platform.users</c> with non-trivial write logic (lockout stamping),
/// which is exactly where the Repository pattern earns its place. Read-only list/lookup queries that
/// only project shapes bypass this and hit the DbContext directly in query handlers.
/// </summary>
public interface IUserRepository
{
    Task<User?> GetByEmailAsync(string email, CancellationToken ct);
    Task<User?> GetByIdAsync(Guid userId, CancellationToken ct);
    Task AddAsync(User user, CancellationToken ct);

    /// <summary>Persists login bookkeeping (last_login, failed_login_count, locked_until) outside the request UoW so it commits even on auth failure.</summary>
    Task UpdateLoginStateAsync(User user, CancellationToken ct);
}

/// <summary>Tenant lookups + the tenants a user may switch into (joined through user_tenant_roles).</summary>
public interface ITenantRepository
{
    Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<Tenant>> ListAsync(int skip, int take, CancellationToken ct);
    Task<IReadOnlyList<UserTenantMembership>> GetMembershipsAsync(Guid userId, CancellationToken ct);
}

public sealed record UserTenantMembership(Guid TenantId, string TenantCode, string DisplayName, string TenantType, bool IsPrimary);

/// <summary>
/// Reads and writes <c>roles</c>, <c>user_tenant_roles</c> and <c>user_permission_overrides</c> (admin surface).
/// <para>
/// WRITES no longer issue direct EF INSERT/UPDATE. database/11_rbac_hardening.sql enables Row-Level Security
/// on these tables and exposes SECURITY DEFINER functions in schema <c>platform</c> that enforce the
/// privilege-escalation (grant-option) guard at the database. The four write paths therefore CALL those
/// functions (passing the authenticated actor) and return the id the function generated; the functions
/// <c>RAISE EXCEPTION</c> with SQLSTATE 42501 when the actor isn't allowed, which the repository translates
/// into the house <see cref="mediq.Utilities.Exceptions.ForbiddenException"/> (→ 403), and SQLSTATE 23000
/// (SoD trigger) into <see cref="mediq.Utilities.Exceptions.ConflictException"/> (→ 409).
/// </para>
/// </summary>
public interface IRoleAssignmentRepository
{
    // ---- Reads (unchanged — no RLS write guard applies) ----
    Task<IReadOnlyList<Role>> ListRolesAsync(Guid? tenantId, CancellationToken ct);
    Task<bool> RoleKeyExistsAsync(string roleKey, Guid? tenantId, CancellationToken ct);
    Task<UserTenantRole?> FindAssignmentAsync(Guid userId, Guid? tenantId, Guid roleId, CancellationToken ct);
    Task<Guid?> FindPermissionIdAsync(string permissionKey, CancellationToken ct);
    Task<UserPermissionOverride?> FindOverrideAsync(Guid userId, Guid permissionId, Guid? tenantId, CancellationToken ct);

    // ---- Writes (via platform.* SECURITY DEFINER functions; actor = authenticated principal) ----

    /// <summary>Calls <c>platform.create_custom_role</c>; returns the new role_id.</summary>
    Task<Guid> CreateCustomRoleAsync(
        Guid actorUserId, string roleKey, string name, string? description, Guid? tenantId, string scope, CancellationToken ct);

    /// <summary>Calls <c>platform.assign_role_to_user</c>; returns user_tenant_role_id (idempotent: ON CONFLICT un-revokes).</summary>
    Task<Guid> AssignRoleAsync(Guid actorUserId, Guid userId, Guid roleId, Guid? tenantId, CancellationToken ct);

    /// <summary>Calls <c>platform.revoke_role_assignment</c>; returns false when the assignment was already revoked.</summary>
    Task<bool> RevokeAssignmentAsync(Guid actorUserId, Guid userTenantRoleId, string reason, CancellationToken ct);

    /// <summary>Calls <c>platform.set_user_permission_override</c>; returns the override_id.</summary>
    Task<Guid> SetPermissionOverrideAsync(
        Guid actorUserId, Guid userId, Guid permissionId, Guid? tenantId, bool isAllowed, string reason,
        DateTime? effectiveFrom, DateTime? expiresAt, CancellationToken ct);

    /// <summary>Calls <c>platform.grant_permission_to_role</c> (the matrix checkbox ON). Idempotent upsert.</summary>
    Task GrantPermissionToRoleAsync(
        Guid actorUserId, Guid roleId, Guid permissionId, Guid? tenantId, bool grantable, CancellationToken ct);

    /// <summary>Calls <c>platform.revoke_permission_from_role</c> (the matrix checkbox OFF); false when it was not granted.</summary>
    Task<bool> RevokePermissionFromRoleAsync(
        Guid actorUserId, Guid roleId, Guid permissionId, Guid? tenantId, CancellationToken ct);

    /// <summary>Calls <c>platform.duplicate_role</c>; clones a role into a new custom role, copying grants. Returns the new role_id.</summary>
    Task<Guid> DuplicateRoleAsync(
        Guid actorUserId, Guid sourceRoleId, string newRoleKey, string newName, string? description, Guid? tenantId, CancellationToken ct);
}
