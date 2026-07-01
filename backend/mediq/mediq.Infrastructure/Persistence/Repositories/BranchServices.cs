using mediq.Application.Abstractions;
using mediq.Domain.Platform;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.Utilities.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace mediq.Infrastructure.Persistence.Repositories;

/// <summary>
/// Read-side branch directory. Lists a tenant's live branches, tenant-scoped by the read chain's RLS +
/// an explicit predicate. Branches are an ORGANIZATIONAL DISPLAY attribute — never an access boundary.
/// </summary>
public sealed class BranchDirectory(PlatformDbContext db) : IBranchDirectory
{
    public async Task<IReadOnlyList<BranchDto>> ListActiveByTenantAsync(Guid tenantId, CancellationToken ct)
        => await db.Branches.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.DeletedAt == null)
            .OrderBy(b => b.Name)
            .Select(b => new BranchDto(b.BranchId, b.Name, b.Code, b.IsActive))
            .ToListAsync(ct);
}

/// <summary>
/// Write-side branch creation. Branches confer no permissions, so this is a DIRECT own-tenant EF insert
/// under RLS — the ambient UnitOfWork commits it and the RLS WITH CHECK bounds the row to the acting tenant
/// (a cross-tenant insert would be rejected). No SECURITY DEFINER indirection is needed. See
/// database/11_rbac_hardening.sql.
/// </summary>
public sealed class BranchRepository(PlatformDbContext db, IClock clock) : IBranchRepository
{
    public async Task<Guid> CreateAsync(Guid tenantId, string name, string? code, CancellationToken ct)
    {
        var branch = Branch.Create(tenantId, name, code, clock.UtcNow);
        await db.Branches.AddAsync(branch, ct);
        return branch.BranchId;
    }
}

/// <summary>
/// Membership-scope setter. Resolves the target user's scope-bearing active membership (primary DESC, then
/// earliest granted) via an RLS-scoped read on the ambient tenant transaction, then routes the write through
/// <c>platform.set_membership_scope</c> (SECURITY DEFINER) which re-checks the actor's tenant.users.update,
/// validates the branch, and writes ONLY branch_id/department — never role_id. SQLSTATE 42501/P0002 → 403;
/// 23503 (branch not in this tenant) / other 23xxx → 409.
/// </summary>
public sealed class MembershipScopeWriter(PlatformDbContext db) : IMembershipScopeWriter
{
    private const string SqlStateInsufficientPrivilege = "42501";
    private const string SqlStateNoDataFound           = "P0002";
    private const string SqlStateForeignKeyViolation   = "23503";
    private const string SqlStateIntegrityConstraint   = "23000";

    public async Task<Guid?> FindScopeMembershipAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var ids = await db.UserTenantRoles.AsNoTracking()
            .Where(utr => utr.UserId == userId
                          && utr.TenantId == tenantId
                          && utr.RevokedAt == null
                          && (utr.ExpiresAt == null || utr.ExpiresAt > now))
            .OrderByDescending(utr => utr.IsPrimary)
            .ThenBy(utr => utr.GrantedAt)
            .ThenBy(utr => utr.UserTenantRoleId)
            .Select(utr => (Guid?)utr.UserTenantRoleId)
            .Take(1)
            .ToListAsync(ct);
        return ids.Count > 0 ? ids[0] : null;
    }

    public async Task<Guid> SetScopeAsync(
        Guid actorUserId, Guid userTenantRoleId, Guid? branchId, string? department, CancellationToken ct)
    {
        try
        {
            var rows = await db.Database.SqlQueryRaw<Guid>(
                    "SELECT platform.set_membership_scope(@p0, @p1, @p2, @p3) AS \"Value\"",
                    new NpgsqlParameter("@p0", actorUserId),
                    new NpgsqlParameter("@p1", userTenantRoleId),
                    new NpgsqlParameter("@p2", NpgsqlDbType.Uuid) { Value = (object?)branchId ?? DBNull.Value },
                    new NpgsqlParameter("@p3", NpgsqlDbType.Varchar) { Value = (object?)department ?? DBNull.Value })
                .ToListAsync(ct);
            return rows.First();
        }
        catch (PostgresException pg) when (pg.SqlState is SqlStateInsufficientPrivilege or SqlStateNoDataFound)
        {
            // Actor lacks tenant.users.update, or the membership is out of a tenant scope → 403.
            throw new ForbiddenException(pg.MessageText, pg);
        }
        catch (PostgresException pg) when (pg.SqlState is SqlStateForeignKeyViolation or SqlStateIntegrityConstraint)
        {
            // Branch is not an active branch of this tenant → 409.
            throw new ConflictException(pg.MessageText, pg);
        }
    }
}
