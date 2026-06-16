using mediq.Application.Abstractions;
using mediq.Domain.Platform;
using Microsoft.EntityFrameworkCore;

namespace mediq.Infrastructure.Persistence.Repositories;

/// <summary>Writes to <c>roles</c>, <c>user_tenant_roles</c> and <c>user_permission_overrides</c>; reads roles + permission ids.</summary>
public sealed class RoleAssignmentRepository(PlatformDbContext db) : IRoleAssignmentRepository
{
    public async Task<IReadOnlyList<Role>> ListRolesAsync(Guid? tenantId, CancellationToken ct) =>
        await db.Roles.AsNoTracking()
            .Where(r => r.DeletedAt == null && (r.IsSystem || r.TenantId == tenantId))
            .OrderBy(r => r.Scope).ThenBy(r => r.Name)
            .ToListAsync(ct);

    public async Task AddRoleAsync(Role role, CancellationToken ct) =>
        await db.Roles.AddAsync(role, ct);

    public Task<bool> RoleKeyExistsAsync(string roleKey, Guid? tenantId, CancellationToken ct) =>
        db.Roles.AsNoTracking()
            .AnyAsync(r => r.RoleKey == roleKey && r.TenantId == tenantId && r.DeletedAt == null, ct);

    public Task<UserTenantRole?> FindAssignmentAsync(Guid userId, Guid? tenantId, Guid roleId, CancellationToken ct) =>
        db.UserTenantRoles.FirstOrDefaultAsync(
            x => x.UserId == userId && x.TenantId == tenantId && x.RoleId == roleId, ct);

    /// <summary>Returns a TRACKED assignment so the revoke handler's mutation is committed by the UoW.</summary>
    public Task<UserTenantRole?> GetAssignmentByIdAsync(Guid userTenantRoleId, CancellationToken ct) =>
        db.UserTenantRoles.FirstOrDefaultAsync(x => x.UserTenantRoleId == userTenantRoleId, ct);

    public async Task AddAssignmentAsync(UserTenantRole assignment, CancellationToken ct) =>
        await db.UserTenantRoles.AddAsync(assignment, ct);

    public async Task<Guid?> FindPermissionIdAsync(string permissionKey, CancellationToken ct)
    {
        var id = await db.Permissions.AsNoTracking()
            .Where(p => p.PermissionKey == permissionKey)
            .Select(p => (Guid?)p.PermissionId)
            .FirstOrDefaultAsync(ct);
        return id;
    }

    public Task<UserPermissionOverride?> FindOverrideAsync(Guid userId, Guid permissionId, Guid? tenantId, CancellationToken ct) =>
        db.UserPermissionOverrides.FirstOrDefaultAsync(
            x => x.UserId == userId && x.PermissionId == permissionId && x.TenantId == tenantId, ct);

    public async Task AddOverrideAsync(UserPermissionOverride ovr, CancellationToken ct) =>
        await db.UserPermissionOverrides.AddAsync(ovr, ct);
}
