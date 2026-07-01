using mediq.Application.Abstractions;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.Utilities.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads <c>platform.invitations</c> directly (RLS-scoped) and performs every WRITE through the schema's
/// SECURITY DEFINER functions (database/11_rbac_hardening.sql). RLS blocks direct app-role writes, and the
/// definer functions are the only sanctioned write path — they enforce the actor's <c>tenant.users.create</c>
/// and the R3 no-escalation guard, while <c>accept_invitation</c> runs unauthenticated (the token is the
/// authorization). Calls run on the DbContext connection so they ENLIST in the ambient UnitOfWork transaction
/// (which issued <c>SET LOCAL app.tenant_id</c> for RLS). SQLSTATEs are translated to house exceptions:
/// 42501 → 403, 23505 (duplicate live pending) → 409, P0002 (no acceptable invitation) → 422 with a GENERIC
/// message so accept never enumerates which token/state was wrong.
/// </summary>
public sealed class InvitationRepository(PlatformDbContext db) : IInvitationRepository
{
    private const string SqlStateInsufficientPrivilege = "42501";
    private const string SqlStateUniqueViolation       = "23505";
    private const string SqlStateNoData                = "P0002"; // no_data_found (raised by resend/accept)

    // ---- Reads -------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<InvitationDto>> ListAsync(Guid tenantId, string? status, CancellationToken ct) =>
        // RLS (invitations_read via rls_can_see_tenant) already bounds the rows to the caller's tenant; the
        // explicit tenant_id predicate is defence-in-depth. LEFT JOIN roles for the display name (never the
        // token/hash — those columns are not projected). ::text casts keep the untyped @p_status NULL filter
        // from tripping Npgsql's unknown-type inference.
        await db.Database.SqlQueryRaw<InvitationDto>(
                """
                SELECT i.invitation_id       AS "InvitationId",
                       i.invited_email::text AS "InvitedEmail",
                       i.role_id             AS "RoleId",
                       r.name                AS "RoleName",
                       i.status              AS "Status",
                       i.expires_at          AS "ExpiresAt",
                       i.resend_count        AS "ResendCount",
                       i.invited_by_user_id  AS "InvitedByUserId",
                       i.accepted_user_id    AS "AcceptedUserId",
                       i.accepted_at         AS "AcceptedAt",
                       i.revoked_at          AS "RevokedAt",
                       i.created_at          AS "CreatedAt"
                FROM platform.invitations i
                LEFT JOIN platform.roles r ON r.role_id = i.role_id
                WHERE i.tenant_id = @p_tenant::uuid
                  AND (@p_status::text IS NULL OR i.status = @p_status::text)
                ORDER BY i.created_at DESC
                """,
                new NpgsqlParameter("@p_tenant", tenantId),
                new NpgsqlParameter("@p_status", (object?)status ?? DBNull.Value))
            .ToListAsync(ct);

    // ---- Writes (SECURITY DEFINER functions; enlisted in the ambient UoW transaction) --------------

    public Task<Guid> CreateAsync(
        Guid actorUserId, Guid tenantId, string invitedEmail, Guid? roleId, string tokenHash,
        DateTime expiresAt, CancellationToken ct) =>
        ScalarAsync<Guid>(
            "SELECT platform.create_invitation(@p0, @p1, @p2, @p3, @p4, @p5) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", tenantId),
            new NpgsqlParameter("@p2", invitedEmail),
            new NpgsqlParameter("@p3", tokenHash),
            new NpgsqlParameter("@p4", expiresAt),
            new NpgsqlParameter("@p5", (object?)roleId ?? DBNull.Value));

    public Task<Guid> ResendAsync(
        Guid actorUserId, Guid tenantId, Guid invitationId, string newTokenHash, DateTime newExpiresAt, CancellationToken ct) =>
        ScalarAsync<Guid>(
            "SELECT platform.resend_invitation(@p0, @p1, @p2, @p3, @p4) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", tenantId),
            new NpgsqlParameter("@p2", invitationId),
            new NpgsqlParameter("@p3", newTokenHash),
            new NpgsqlParameter("@p4", newExpiresAt));

    public Task<bool> RevokeAsync(Guid actorUserId, Guid tenantId, Guid invitationId, CancellationToken ct) =>
        ScalarAsync<bool>(
            "SELECT platform.revoke_invitation(@p0, @p1, @p2) AS \"Value\"",
            ct,
            new NpgsqlParameter("@p0", actorUserId),
            new NpgsqlParameter("@p1", tenantId),
            new NpgsqlParameter("@p2", invitationId));

    public async Task<(Guid UserId, Guid TenantId, bool AlreadyExisted)> AcceptAsync(
        string tokenHash, string passwordHash, string displayName, CancellationToken ct)
    {
        try
        {
            var rows = await db.Database.SqlQueryRaw<AcceptRow>(
                    """
                    SELECT out_user_id AS "UserId", out_tenant_id AS "TenantId", out_already_existed AS "AlreadyExisted"
                    FROM platform.accept_invitation(@p0, @p1, @p2)
                    """,
                    new NpgsqlParameter("@p0", tokenHash),
                    new NpgsqlParameter("@p1", passwordHash),
                    new NpgsqlParameter("@p2", displayName))
                .ToListAsync(ct);
            var r = rows.First();
            return (r.UserId, r.TenantId, r.AlreadyExisted);
        }
        catch (PostgresException pg) when (pg.SqlState == SqlStateNoData)
        {
            // Garbage / expired / revoked / already-used all raise the SAME no_data_found — return one generic
            // message so accept cannot be used to enumerate valid tokens or invitation states.
            throw new BusinessRuleException("This invitation is invalid, expired, or has already been used.", pg);
        }
    }

    private sealed record AcceptRow(Guid UserId, Guid TenantId, bool AlreadyExisted);

    private async Task<T> ScalarAsync<T>(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
        try
        {
            var rows = await db.Database.SqlQueryRaw<T>(sql, parameters).ToListAsync(ct);
            return rows.First();
        }
        catch (PostgresException pg) when (pg.SqlState == SqlStateInsufficientPrivilege)
        {
            throw new ForbiddenException(pg.MessageText, pg);   // actor may not invite / confer the role → 403
        }
        catch (PostgresException pg) when (pg.SqlState == SqlStateUniqueViolation)
        {
            throw new ConflictException("An active invitation already exists for this email.", pg);   // 409
        }
        catch (PostgresException pg) when (pg.SqlState == SqlStateNoData)
        {
            throw new BusinessRuleException("Invitation not found or not pending.", pg);   // resend on non-pending → 422
        }
    }
}
