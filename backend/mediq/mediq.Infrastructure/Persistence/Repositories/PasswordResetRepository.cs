using mediq.Application.Abstractions;
using mediq.Utilities.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Persistence.Repositories;

/// <summary>
/// Performs every password-reset WRITE through the schema's SECURITY DEFINER functions
/// (database/12_password_reset.sql): the app role (docslot_app) is denied direct INSERT/UPDATE on
/// <c>platform.password_reset_tokens</c>, so the definer functions (running as owner) are the only sanctioned
/// path. They enforce the actor's <c>tenant.users.update</c> + the R3 no-escalation guard (admin mint) and
/// redeem unauthenticated (consume). Calls run on the DbContext connection so they ENLIST in the ambient
/// UnitOfWork transaction. SQLSTATEs are translated to house exceptions: 42501 → 403, P0002 (no acceptable
/// row) → 422 with a GENERIC message so redemption never enumerates which token/state was wrong.
/// </summary>
public sealed class PasswordResetRepository(PlatformDbContext db) : IPasswordResetRepository
{
    private const string SqlStateInsufficientPrivilege = "42501";
    private const string SqlStateNoData                = "P0002"; // no_data_found (raised by admin mint / consume)

    public Task<Guid> RequestAsync(
        Guid userId, string tokenHash, string? requestedIp, DateTime expiresAt, CancellationToken ct) =>
        ScalarAsync<Guid>(
            "SELECT platform.request_password_reset(@p0, @p1, @p2, @p3) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", userId),
            new NpgsqlParameter("@p1", tokenHash),
            new NpgsqlParameter("@p2", (object?)requestedIp ?? DBNull.Value),
            new NpgsqlParameter("@p3", expiresAt));

    public Task<Guid> AdminRequestAsync(
        Guid actorUserId, Guid targetUserId, string tokenHash, string? requestedIp, DateTime expiresAt,
        Guid? tenantId, CancellationToken ct) =>
        ScalarAsync<Guid>(
            "SELECT platform.admin_request_password_reset(@p0, @p1, @p2, @p3, @p4, @p5) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", targetUserId),
            new NpgsqlParameter("@p2", tokenHash),
            new NpgsqlParameter("@p3", (object?)requestedIp ?? DBNull.Value),
            new NpgsqlParameter("@p4", expiresAt),
            new NpgsqlParameter("@p5", (object?)tenantId ?? DBNull.Value));

    public async Task<Guid> ConsumeAsync(string tokenHash, string passwordHash, CancellationToken ct)
    {
        try
        {
            return await ScalarAsync<Guid>(
                "SELECT platform.consume_password_reset(@p0, @p1) AS \"Value\"",
                ct,
                new NpgsqlParameter("@p0", tokenHash),
                new NpgsqlParameter("@p1", passwordHash));
        }
        catch (BusinessRuleException)
        {
            // ScalarAsync already mapped P0002 → generic BusinessRuleException. Re-throw the generic message so
            // the reset endpoint cannot be used to enumerate valid tokens or their state.
            throw;
        }
    }

    private async Task<T> ScalarAsync<T>(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        try
        {
            var rows = await db.Database.SqlQueryRaw<T>(sql, parameters).ToListAsync(ct);
            return rows.First();
        }
        catch (PostgresException pg) when (pg.SqlState == SqlStateInsufficientPrivilege)
        {
            throw new ForbiddenException(pg.MessageText, pg);   // actor may not reset this user → 403
        }
        catch (PostgresException pg) when (pg.SqlState == SqlStateNoData)
        {
            // admin mint: unknown/non-member target; consume: garbage/expired/used token — one generic 422.
            throw new BusinessRuleException("This reset link is invalid or has expired.", pg);
        }
    }
}
