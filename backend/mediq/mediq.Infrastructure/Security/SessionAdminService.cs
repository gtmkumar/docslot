using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.Security;
using mediq.Utilities.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Admin oversight of <c>platform.user_sessions</c> (issue #87). The list and single-session revoke are
/// tenant-scoped on BOTH the session's <c>active_tenant_id</c> = caller tenant AND an active
/// <c>user_tenant_roles</c> membership of the owner, so a multi-tenant user's other-tenant session neither
/// lists nor revokes here (audit hardening). Sign-out-all is deliberately global (incident kill switch) after
/// the same membership check. Only session METADATA crosses the wire — token/refresh hashes
/// are never projected. Writes run on the request's <see cref="PlatformDbContext"/> so they commit inside the
/// command's UnitOfWork transaction (alongside the audit row), unlike the theft-mitigation
/// <see cref="SessionStore.RevokeAllForUserAsync"/> which deliberately uses a survive-rollback connection.
/// </summary>
public sealed class SessionAdminService(PlatformDbContext db, IGeoIpResolver geo) : ISessionAdminService
{
    public async Task<IReadOnlyList<ActiveSessionDto>> ListActiveForTenantAsync(
        Guid tenantId, Guid? currentUserId, int take, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<SessionRow>(
            """
            SELECT s.session_id AS "SessionId", s.user_id AS "UserId", u.full_name AS "UserName",
                   u.email::text AS "UserEmail", s.ip_address::text AS "IpAddress",
                   s.issued_at AS "StartedAt", s.last_activity_at AS "LastActivityAt", s.expires_at AS "ExpiresAt"
            FROM platform.user_sessions s
            JOIN platform.users u ON u.user_id = s.user_id
            WHERE s.revoked_at IS NULL
              AND s.expires_at > NOW()
              -- audit hardening (#87 MEDIUM): the session must have been ESTABLISHED under the caller's
              -- tenant, not merely owned by a member — else a multi-tenant user's other-tenant session leaks.
              AND s.active_tenant_id = @p0
              AND EXISTS (
                    SELECT 1 FROM platform.user_tenant_roles utr
                    WHERE utr.user_id = s.user_id AND utr.tenant_id = @p0
                      AND utr.revoked_at IS NULL
                      AND (utr.expires_at IS NULL OR utr.expires_at > NOW()))
            ORDER BY s.last_activity_at DESC
            LIMIT @p1
            """,
            new NpgsqlParameter("@p0", tenantId), new NpgsqlParameter("@p1", take))
            .ToListAsync(ct);

        // #94: resolve each distinct IP to a city via the seam. Offline (NullGeoIpResolver) → all null, so the
        // UI shows just the IP; a live provider fills City without any other change.
        var cities = await ResolveCitiesAsync(rows.Select(r => r.IpAddress), ct);

        return rows.Select(r => new ActiveSessionDto(
            r.SessionId, r.UserId, r.UserName, r.UserEmail, r.IpAddress,
            Utc(r.StartedAt), Utc(r.LastActivityAt), Utc(r.ExpiresAt),
            currentUserId is not null && r.UserId == currentUserId.Value,
            City(cities, r.IpAddress))).ToList();
    }

    /// <summary>Resolve the distinct non-empty IPs to a city map (one lookup per unique IP; null offline).</summary>
    private async Task<Dictionary<string, string?>> ResolveCitiesAsync(IEnumerable<string?> ips, CancellationToken ct)
    {
        var map = new Dictionary<string, string?>();
        foreach (var ip in ips.Where(ip => !string.IsNullOrEmpty(ip)).Distinct())
            map[ip!] = await geo.ResolveCityAsync(ip, ct);
        return map;
    }

    private static string? City(IReadOnlyDictionary<string, string?> map, string? ip) =>
        ip is not null && map.TryGetValue(ip, out var c) ? c : null;

    public async Task<bool> RevokeMemberSessionAsync(Guid sessionId, Guid tenantId, string reason, CancellationToken ct)
    {
        // Single-statement, membership-guarded revoke: only flips a row whose owner is an active member of the
        // caller's tenant. Affects 0 rows when the session is absent, already revoked, or owned by a non-member
        // (the caller treats that as a refusal / 404) — so a non-member's session can never be reached.
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE platform.user_sessions s
            SET revoked_at = NOW(), revoked_reason = @p2
            WHERE s.session_id = @p0
              AND s.revoked_at IS NULL
              AND s.active_tenant_id = @p1
              AND EXISTS (
                    SELECT 1 FROM platform.user_tenant_roles utr
                    WHERE utr.user_id = s.user_id AND utr.tenant_id = @p1
                      AND utr.revoked_at IS NULL
                      AND (utr.expires_at IS NULL OR utr.expires_at > NOW()))
            """,
            new NpgsqlParameter("@p0", sessionId),
            new NpgsqlParameter("@p1", tenantId),
            new NpgsqlParameter("@p2", reason));
        return affected > 0;
    }

    public async Task<int> RevokeAllForMemberAsync(Guid targetUserId, Guid tenantId, string reason, CancellationToken ct)
    {
        // Refuse outright if the target is not an active member of the caller's tenant (cross-tenant guard).
        var isMember = await db.Database.SqlQueryRaw<BoolRow>(
            """
            SELECT EXISTS (
                SELECT 1 FROM platform.user_tenant_roles utr
                WHERE utr.user_id = @p0 AND utr.tenant_id = @p1
                  AND utr.revoked_at IS NULL
                  AND (utr.expires_at IS NULL OR utr.expires_at > NOW())) AS "Value"
            """,
            new NpgsqlParameter("@p0", targetUserId), new NpgsqlParameter("@p1", tenantId))
            .ToListAsync(ct);

        if (!(isMember.FirstOrDefault()?.Value ?? false))
            throw new ForbiddenException("The target user is not a member of this tenant.");

        // Membership already confirmed above; revoke every live session the user holds ACROSS ALL tenants
        // (sign-out-everywhere). DELIBERATELY global — unlike list/single-revoke which scope by
        // active_tenant_id — because this is the incident kill switch for a compromised account.
        return await db.Database.ExecuteSqlRawAsync(
            "UPDATE platform.user_sessions SET revoked_at = NOW(), revoked_reason = @p1 "
            + "WHERE user_id = @p0 AND revoked_at IS NULL AND expires_at > NOW()",
            new NpgsqlParameter("@p0", targetUserId),
            new NpgsqlParameter("@p1", reason));
    }

    private static DateTimeOffset Utc(DateTime dt) => new(DateTime.SpecifyKind(dt, DateTimeKind.Utc));

    private sealed record SessionRow(
        Guid SessionId, Guid UserId, string UserName, string? UserEmail, string? IpAddress,
        DateTime StartedAt, DateTime LastActivityAt, DateTime ExpiresAt);
    private sealed record BoolRow(bool Value);
}
