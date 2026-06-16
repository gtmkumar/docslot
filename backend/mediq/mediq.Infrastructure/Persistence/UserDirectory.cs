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
        var rows = await (
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

        return rows
            .Select(x => new UserListItemDto(x.UserId, x.Email, x.FullName, x.Phone, x.IsActive, x.MfaEnabled, x.LastLoginAt))
            .ToList();
    }
}
