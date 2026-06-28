using mediq.Application.Abstractions;
using mediq.Utilities.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Persistence.Repositories;

/// <summary>
/// User-lifecycle writes (deactivate/reactivate, edit profile, reset access). Every operation is a SECURITY
/// DEFINER function (database/11_rbac_hardening.sql) that re-checks the actor's permission, scopes the target
/// to the tenant by membership, self-guards, and enforces the last-admin guard — so the database is the
/// authorization boundary regardless of RLS. Calls enlist in the ambient UnitOfWork transaction (matching
/// RoleAssignmentRepository), so the mutation + its audit row commit atomically.
/// <para>
/// SQLSTATE translation keeps the raw DB text out of a leaky 500: 42501 (privilege guard) and P0002 (target
/// is not a member of this tenant — out of the actor's scope) → 403; 23000/23505 (last-admin / SoD / unique)
/// → 409. The DB's explanatory message is preserved for the client toast.
/// </para>
/// </summary>
public sealed class UserLifecycle(PlatformDbContext db) : IUserLifecycle
{
    private const string SqlStateInsufficientPrivilege = "42501"; // permission / self / no-escalation guard
    private const string SqlStateIntegrityConstraint    = "23000"; // last-admin / SoD trigger
    private const string SqlStateUniqueViolation        = "23505";
    private const string SqlStateNoDataFound            = "P0002"; // target not a member of the tenant

    public Task SetActiveAsync(
        Guid actorUserId, Guid targetUserId, Guid tenantId, bool isActive, string reason, CancellationToken ct) =>
        ExecAsync(
            "SELECT platform.set_tenant_user_active(@p0, @p1, @p2, @p3, @p4)", ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", targetUserId),
            new NpgsqlParameter("@p2", tenantId),
            new NpgsqlParameter("@p3", isActive),
            new NpgsqlParameter("@p4", reason));

    public Task UpdateProfileAsync(
        Guid actorUserId, Guid targetUserId, Guid tenantId, string fullName, string? phone, string preferredLanguage,
        CancellationToken ct) =>
        ExecAsync(
            "SELECT platform.update_user_profile(@p0, @p1, @p2, @p3, @p4, @p5)", ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", targetUserId),
            new NpgsqlParameter("@p2", tenantId),
            new NpgsqlParameter("@p3", fullName),
            new NpgsqlParameter("@p4", (object?)phone ?? DBNull.Value),
            new NpgsqlParameter("@p5", preferredLanguage));

    public Task ResetAccessAsync(
        Guid actorUserId, Guid targetUserId, Guid tenantId, string reason, CancellationToken ct) =>
        ExecAsync(
            "SELECT platform.reset_user_access(@p0, @p1, @p2, @p3)", ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", targetUserId),
            new NpgsqlParameter("@p2", tenantId),
            new NpgsqlParameter("@p3", reason));

    private async Task ExecAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
        }
        catch (PostgresException pg) when (pg.SqlState is SqlStateInsufficientPrivilege or SqlStateNoDataFound)
        {
            throw new ForbiddenException(pg.MessageText, pg);
        }
        catch (PostgresException pg) when (pg.SqlState is SqlStateIntegrityConstraint or SqlStateUniqueViolation)
        {
            throw new ConflictException(pg.MessageText, pg);
        }
    }
}
