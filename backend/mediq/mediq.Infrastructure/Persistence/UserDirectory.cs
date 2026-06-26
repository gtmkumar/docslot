using mediq.Application.Abstractions;
using mediq.SharedDataModel.Docslot.Admin;
using Microsoft.EntityFrameworkCore;

namespace mediq.Infrastructure.Persistence;

/// <summary>
/// Read-side user directory. Lists users in a tenant by joining <c>user_tenant_roles</c> (active) to
/// <c>users</c>, projecting straight to the DTO (CQRS read trade-off — no aggregate loading).
/// </summary>
public sealed class UserDirectory(PlatformDbContext db) : IUserDirectory
{
    public async Task<IReadOnlyList<UserListItemDto>> ListByTenantAsync(Guid tenantId, int skip, int take, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // 1) The page of distinct users with an active membership in the tenant.
        var users = await (
            from utr in db.UserTenantRoles.AsNoTracking()
            join u in db.Users.AsNoTracking() on utr.UserId equals u.UserId
            where utr.TenantId == tenantId
                  && utr.RevokedAt == null
                  && (utr.ExpiresAt == null || utr.ExpiresAt > now)
                  && u.DeletedAt == null
            select new { u.UserId, u.Email, u.FullName, u.Phone, u.IsActive, u.MfaEnabled, u.LastLoginAt })
            .Distinct()
            .OrderBy(x => x.FullName)
            .Skip(skip).Take(take)
            .ToListAsync(ct);

        var userIds = users.Select(u => u.UserId).ToList();

        // 2) Their active role assignments in this tenant (one user → many roles), joined to the role name.
        var roleRows = await (
            from utr in db.UserTenantRoles.AsNoTracking()
            join r in db.Roles.AsNoTracking() on utr.RoleId equals r.RoleId
            where utr.TenantId == tenantId
                  && utr.RevokedAt == null
                  && (utr.ExpiresAt == null || utr.ExpiresAt > now)
                  && r.DeletedAt == null
                  && userIds.Contains(utr.UserId)
            select new
            {
                utr.UserId, utr.UserTenantRoleId, r.RoleId, r.RoleKey, r.Name, utr.IsPrimary, utr.ExpiresAt,
            })
            .ToListAsync(ct);

        var rolesByUser = roleRows
            .GroupBy(x => x.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<UserRoleDto>)g
                    .OrderByDescending(x => x.IsPrimary).ThenBy(x => x.Name)
                    .Select(x => new UserRoleDto(x.UserTenantRoleId, x.RoleId, x.RoleKey, x.Name, x.IsPrimary, x.ExpiresAt))
                    .ToList());

        return users
            .Select(x => new UserListItemDto(
                x.UserId, x.Email, x.FullName, x.Phone, x.IsActive, x.MfaEnabled, x.LastLoginAt,
                rolesByUser.TryGetValue(x.UserId, out var rs) ? rs : []))
            .ToList();
    }
}
