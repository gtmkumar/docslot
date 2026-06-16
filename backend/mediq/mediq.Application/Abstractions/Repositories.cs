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

/// <summary>Writes to <c>roles</c>, <c>user_tenant_roles</c> and <c>user_permission_overrides</c> (admin surface).</summary>
public interface IRoleAssignmentRepository
{
    Task<IReadOnlyList<Role>> ListRolesAsync(Guid? tenantId, CancellationToken ct);
    Task AddRoleAsync(Role role, CancellationToken ct);
    Task<bool> RoleKeyExistsAsync(string roleKey, Guid? tenantId, CancellationToken ct);

    Task<UserTenantRole?> FindAssignmentAsync(Guid userId, Guid? tenantId, Guid roleId, CancellationToken ct);
    Task<UserTenantRole?> GetAssignmentByIdAsync(Guid userTenantRoleId, CancellationToken ct);
    Task AddAssignmentAsync(UserTenantRole assignment, CancellationToken ct);

    Task<Guid?> FindPermissionIdAsync(string permissionKey, CancellationToken ct);
    Task<UserPermissionOverride?> FindOverrideAsync(Guid userId, Guid permissionId, Guid? tenantId, CancellationToken ct);
    Task AddOverrideAsync(UserPermissionOverride ovr, CancellationToken ct);
}
