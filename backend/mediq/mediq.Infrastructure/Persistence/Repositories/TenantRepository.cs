using mediq.Application.Abstractions;
using mediq.Domain.Platform;
using Microsoft.EntityFrameworkCore;

namespace mediq.Infrastructure.Persistence.Repositories;

public sealed class TenantRepository(PlatformDbContext db) : ITenantRepository
{
    public Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken ct) =>
        db.Tenants.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.DeletedAt == null, ct);

    public async Task<IReadOnlyList<Tenant>> ListAsync(int skip, int take, CancellationToken ct) =>
        await db.Tenants.AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .OrderBy(t => t.DisplayName)
            .Skip(skip).Take(take)
            .ToListAsync(ct);

    /// <summary>
    /// The tenants a user can switch into: active <c>user_tenant_roles</c> joined to tenants. Projected
    /// directly (read-side) — bypasses aggregate loading.
    /// </summary>
    public async Task<IReadOnlyList<UserTenantMembership>> GetMembershipsAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await (
            from utr in db.UserTenantRoles.AsNoTracking()
            join t in db.Tenants.AsNoTracking() on utr.TenantId equals t.TenantId
            where utr.UserId == userId
                  && utr.RevokedAt == null
                  && (utr.ExpiresAt == null || utr.ExpiresAt > now)
                  && t.DeletedAt == null
            select new { t.TenantId, t.TenantCode, t.DisplayName, t.TenantType, utr.IsPrimary })
            .Distinct()
            .ToListAsync(ct);

        return rows
            .Select(r => new UserTenantMembership(r.TenantId, r.TenantCode, r.DisplayName, r.TenantType, r.IsPrimary))
            .ToList();
    }
}
