using mediq.Application.Abstractions;
using mediq.Domain.Platform;
using mediq.Utilities.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads roles + permission ids directly; performs all WRITES through the schema's SECURITY DEFINER
/// functions (database/11_rbac_hardening.sql). RLS is enabled on roles / user_tenant_roles /
/// user_permission_overrides, so app-role inserts are blocked — the definer functions are the only
/// sanctioned write path and they enforce the grant-option (privilege-escalation) guard at the database.
/// <para>
/// The function calls run via <see cref="Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions"/>
/// on the DbContext connection, so they ENLIST in the ambient UnitOfWork transaction (which already issued
/// <c>SET LOCAL app.tenant_id</c> for RLS) — matching the house pattern used by SlotHoldService and
/// RbacQueryService. A <see cref="PostgresException"/> with SQLSTATE 42501 (insufficient_privilege) is
/// translated to <see cref="ForbiddenException"/> (→ 403) and 23000 (SoD integrity constraint) to
/// <see cref="ConflictException"/> (→ 409), so the raw DB text never leaks to the client.
/// </para>
/// </summary>
public sealed class RoleAssignmentRepository(PlatformDbContext db) : IRoleAssignmentRepository
{
    private const string SqlStateInsufficientPrivilege = "42501"; // grant-option / escalation guard
    private const string SqlStateIntegrityConstraint    = "23000"; // SoD role_incompatibility trigger
    private const string SqlStateUniqueViolation        = "23505"; // duplicate catalog key (module/permission)

    // ---- Reads -------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Role>> ListRolesAsync(Guid? tenantId, CancellationToken ct) =>
        await db.Roles.AsNoTracking()
            .Where(r => r.DeletedAt == null && (r.IsSystem || r.TenantId == tenantId))
            .OrderBy(r => r.Scope).ThenBy(r => r.Name)
            .ToListAsync(ct);

    public Task<bool> RoleKeyExistsAsync(string roleKey, Guid? tenantId, CancellationToken ct) =>
        db.Roles.AsNoTracking()
            .AnyAsync(r => r.RoleKey == roleKey && r.TenantId == tenantId && r.DeletedAt == null, ct);

    public Task<UserTenantRole?> FindAssignmentAsync(Guid userId, Guid? tenantId, Guid roleId, CancellationToken ct) =>
        db.UserTenantRoles.AsNoTracking().FirstOrDefaultAsync(
            x => x.UserId == userId && x.TenantId == tenantId && x.RoleId == roleId, ct);

    public async Task<Guid?> FindPermissionIdAsync(string permissionKey, CancellationToken ct)
    {
        var id = await db.Permissions.AsNoTracking()
            .Where(p => p.PermissionKey == permissionKey)
            .Select(p => (Guid?)p.PermissionId)
            .FirstOrDefaultAsync(ct);
        return id;
    }

    public Task<UserPermissionOverride?> FindOverrideAsync(Guid userId, Guid permissionId, Guid? tenantId, CancellationToken ct) =>
        db.UserPermissionOverrides.AsNoTracking().FirstOrDefaultAsync(
            x => x.UserId == userId && x.PermissionId == permissionId && x.TenantId == tenantId, ct);

    // ---- Writes (SECURITY DEFINER functions; enlisted in the ambient UoW transaction) --------------

    public Task<Guid> CreateCustomRoleAsync(
        Guid actorUserId, string roleKey, string name, string? description, Guid? tenantId, string scope, CancellationToken ct) =>
        ScalarAsync<Guid>(
            "SELECT platform.create_custom_role(@p0, @p1, @p2, @p3, @p4, @p5) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", roleKey),
            new NpgsqlParameter("@p2", name),
            new NpgsqlParameter("@p3", (object?)description ?? DBNull.Value),
            new NpgsqlParameter("@p4", (object?)tenantId ?? DBNull.Value),
            new NpgsqlParameter("@p5", scope));

    public Task<Guid> AssignRoleAsync(Guid actorUserId, Guid userId, Guid roleId, Guid? tenantId, CancellationToken ct) =>
        ScalarAsync<Guid>(
            "SELECT platform.assign_role_to_user(@p0, @p1, @p2, @p3) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", userId),
            new NpgsqlParameter("@p2", roleId),
            new NpgsqlParameter("@p3", (object?)tenantId ?? DBNull.Value));

    public Task<bool> RevokeAssignmentAsync(Guid actorUserId, Guid userTenantRoleId, string reason, CancellationToken ct) =>
        ScalarAsync<bool>(
            "SELECT platform.revoke_role_assignment(@p0, @p1, @p2) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", userTenantRoleId),
            new NpgsqlParameter("@p2", reason));

    public Task<Guid> SetPermissionOverrideAsync(
        Guid actorUserId, Guid userId, Guid permissionId, Guid? tenantId, bool isAllowed, string reason,
        DateTime? effectiveFrom, DateTime? expiresAt, CancellationToken ct) =>
        ScalarAsync<Guid>(
            "SELECT platform.set_user_permission_override(@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", userId),
            new NpgsqlParameter("@p2", permissionId),
            new NpgsqlParameter("@p3", (object?)tenantId ?? DBNull.Value),
            new NpgsqlParameter("@p4", isAllowed),
            new NpgsqlParameter("@p5", reason),
            new NpgsqlParameter("@p6", (object?)effectiveFrom ?? DBNull.Value),
            new NpgsqlParameter("@p7", (object?)expiresAt ?? DBNull.Value));

    public Task GrantPermissionToRoleAsync(
        Guid actorUserId, Guid roleId, Guid permissionId, Guid? tenantId, bool grantable, CancellationToken ct) =>
        ExecAsync(
            "SELECT platform.grant_permission_to_role(@p0, @p1, @p2, @p3, @p4)",
            ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", roleId),
            new NpgsqlParameter("@p2", permissionId),
            new NpgsqlParameter("@p3", (object?)tenantId ?? DBNull.Value),
            new NpgsqlParameter("@p4", grantable));

    public Task<bool> RevokePermissionFromRoleAsync(
        Guid actorUserId, Guid roleId, Guid permissionId, Guid? tenantId, CancellationToken ct) =>
        ScalarAsync<bool>(
            "SELECT platform.revoke_permission_from_role(@p0, @p1, @p2, @p3) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", roleId),
            new NpgsqlParameter("@p2", permissionId),
            new NpgsqlParameter("@p3", (object?)tenantId ?? DBNull.Value));

    public Task<Guid> DuplicateRoleAsync(
        Guid actorUserId, Guid sourceRoleId, string newRoleKey, string newName, string? description, Guid? tenantId, CancellationToken ct) =>
        ScalarAsync<Guid>(
            "SELECT platform.duplicate_role(@p0, @p1, @p2, @p3, @p4, @p5) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", sourceRoleId),
            new NpgsqlParameter("@p2", newRoleKey),
            new NpgsqlParameter("@p3", newName),
            new NpgsqlParameter("@p4", (object?)description ?? DBNull.Value),
            new NpgsqlParameter("@p5", (object?)tenantId ?? DBNull.Value));

    public Task<Guid> CreateResourceTypeAsync(
        Guid actorUserId, string resourceKey, string name, string? description, int displayOrder, CancellationToken ct) =>
        ScalarAsync<Guid>(
            "SELECT platform.create_resource_type(@p0, @p1, @p2, @p3, @p4) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", resourceKey),
            new NpgsqlParameter("@p2", name),
            new NpgsqlParameter("@p3", (object?)description ?? DBNull.Value),
            new NpgsqlParameter("@p4", displayOrder));

    public Task<Guid> CreatePermissionAsync(
        Guid actorUserId, string permissionKey, string resource, string action, string scope, string description,
        bool isDangerous, CancellationToken ct) =>
        ScalarAsync<Guid>(
            "SELECT platform.create_permission(@p0, @p1, @p2, @p3, @p4, @p5, @p6) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", permissionKey),
            new NpgsqlParameter("@p2", resource),
            new NpgsqlParameter("@p3", action),
            new NpgsqlParameter("@p4", scope),
            new NpgsqlParameter("@p5", description),
            new NpgsqlParameter("@p6", isDangerous));

    public Task<Guid> SetModuleLicenseAsync(
        Guid actorUserId, Guid tenantId, Guid resourceTypeId, bool isLicensed, string? reason, CancellationToken ct) =>
        ScalarAsync<Guid>(
            "SELECT platform.set_module_license(@p0, @p1, @p2, @p3, @p4) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", tenantId),
            new NpgsqlParameter("@p2", resourceTypeId),
            new NpgsqlParameter("@p3", isLicensed),
            new NpgsqlParameter("@p4", (object?)reason ?? DBNull.Value));

    /// <summary>
    /// Runs a SECURITY DEFINER function that returns void (e.g. grant_permission_to_role) on the DbContext
    /// connection (enlisted in the ambient UoW transaction), translating the privilege/SoD SQLSTATEs the
    /// same way as <see cref="ScalarAsync{T}"/>.
    /// </summary>
    private async Task ExecAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
        }
        catch (PostgresException pg) when (pg.SqlState == SqlStateInsufficientPrivilege)
        {
            throw new ForbiddenException(pg.MessageText, pg);
        }
        catch (PostgresException pg) when (pg.SqlState is SqlStateIntegrityConstraint or SqlStateUniqueViolation)
        {
            throw new ConflictException(pg.MessageText, pg);
        }
    }

    /// <summary>
    /// Runs a single-row, single-column function SELECT on the DbContext connection (enlisted in the ambient
    /// UoW transaction) and returns the scalar. Translates the privilege/SoD SQLSTATEs into house exceptions
    /// so the API returns 403/409 instead of a leaky 500.
    /// </summary>
    private async Task<T> ScalarAsync<T>(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        try
        {
            var rows = await db.Database.SqlQueryRaw<T>(sql, parameters).ToListAsync(ct);
            return rows.First();
        }
        catch (PostgresException pg) when (pg.SqlState == SqlStateInsufficientPrivilege)
        {
            // Grant-option / privilege-escalation guard inside the SECURITY DEFINER function → 403.
            throw new ForbiddenException(pg.MessageText, pg);
        }
        catch (PostgresException pg) when (pg.SqlState is SqlStateIntegrityConstraint or SqlStateUniqueViolation)
        {
            // SoD (role_incompatibility) trigger OR a duplicate catalog key (module/permission already
            // exists) → 409 with the DB's explanatory message.
            throw new ConflictException(pg.MessageText, pg);
        }
    }
}
