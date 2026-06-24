using mediq.Application.Abstractions;
using mediq.Utilities.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Persistence.Repositories;

/// <summary>
/// App-side gateway to the impersonation lifecycle (issue #3). Every write goes through the schema's
/// SECURITY DEFINER functions (<c>platform.begin_impersonation</c> / <c>platform.end_impersonation</c> in
/// database/11_rbac_hardening.sql) — the ONLY paths that emit the hash-chained audit row and enforce the
/// permission/authorization guards. The C# layer never touches <c>impersonation_sessions</c> or the GUC.
/// <para>
/// The function calls run on the DbContext connection so they ENLIST in the ambient UnitOfWork transaction
/// (which already issued the per-request <c>SET LOCAL</c> GUCs) — the house pattern shared with
/// <see cref="RoleAssignmentRepository"/>. A <see cref="PostgresException"/> with SQLSTATE 42501
/// (insufficient_privilege — the DB-side permission/authorization guard) is translated to
/// <see cref="ForbiddenException"/> (→ 403) so the raw DB text never leaks to the client.
/// </para>
/// </summary>
public sealed class ImpersonationRepository(PlatformDbContext db) : IImpersonationRepository
{
    private const string SqlStateInsufficientPrivilege = "42501";

    public Task<Guid> BeginAsync(Guid actorUserId, Guid targetTenantId, string reason, Guid? targetUserId,
        TimeSpan ttl, bool breakGlass, CancellationToken ct) =>
        ScalarAsync<Guid>(
            // make_interval(mins => …) keeps the TTL unambiguous across the boundary (no TimeSpan↔interval guessing).
            "SELECT platform.begin_impersonation(@p0, @p1, @p2, @p3, make_interval(mins => @p4), @p5) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", targetTenantId),
            new NpgsqlParameter("@p2", reason),
            new NpgsqlParameter("@p3", (object?)targetUserId ?? DBNull.Value),
            new NpgsqlParameter("@p4", (int)Math.Round(ttl.TotalMinutes)),
            new NpgsqlParameter("@p5", breakGlass));

    public Task<bool> EndAsync(Guid impersonationId, Guid actorUserId, CancellationToken ct) =>
        ScalarAsync<bool>(
            "SELECT platform.end_impersonation(@p0, @p1) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", impersonationId),
            new NpgsqlParameter("@p1", actorUserId));

    /// <summary>
    /// Runs a single-row, single-column function SELECT on the DbContext connection (enlisted in the ambient
    /// UoW transaction) and returns the scalar, translating the DB-side privilege guard (42501) into a 403.
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
            throw new ForbiddenException(pg.MessageText, pg);
        }
    }
}
