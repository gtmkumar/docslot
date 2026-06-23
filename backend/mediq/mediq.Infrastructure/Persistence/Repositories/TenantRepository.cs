using mediq.Application.Abstractions;
using mediq.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
    /// The tenants a user can switch into. This is a LOGIN-TIME, CROSS-TENANT self-read: it runs before any
    /// tenant context (and thus <c>app.tenant_id</c>) exists, so a direct EF read of <c>user_tenant_roles</c>
    /// is now blocked by the Row-Level Security enabled in database/11_rbac_hardening.sql (the <c>utr_read</c>
    /// policy requires the row's tenant to equal <c>current_tenant_id()</c>). The hardening file's design note
    /// mandates that login-time cross-tenant lookups go through a SECURITY DEFINER / owner-rights path.
    /// <para>
    /// We therefore source the switch-list from the purpose-built <c>platform.user_memberships(uuid)</c>
    /// SECURITY DEFINER function (owner-rights, so it bypasses R1 RLS; filtered to the caller's own
    /// <c>user_id</c>, so there is no cross-tenant leak). It preserves the original semantics — every active,
    /// non-deleted membership, including <c>is_primary</c> — rather than only tenants where the user has
    /// resolved permissions. The query runs on the DbContext connection (house pattern:
    /// <see cref="RelationalQueryableExtensions.FromSqlRaw"/>).
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<UserTenantMembership>> GetMembershipsAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.UserMembershipRows
            .FromSqlRaw(
                """
                SELECT
                    tenant_id    AS "TenantId",
                    tenant_code  AS "TenantCode",
                    display_name AS "DisplayName",
                    tenant_type  AS "TenantType",
                    is_primary   AS "IsPrimary"
                FROM platform.user_memberships(@p0)
                """,
                new NpgsqlParameter("@p0", userId))
            .AsNoTracking()
            .ToListAsync(ct);

        return rows
            .Select(r => new UserTenantMembership(r.TenantId, r.TenantCode, r.DisplayName, r.TenantType, r.IsPrimary))
            .ToList();
    }
}
