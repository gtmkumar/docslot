using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Persists/rotates/revokes <c>platform.user_sessions</c>. Only token HASHES are stored (never raw
/// tokens), so a DB leak does not hand an attacker live sessions. Rotation revokes the old refresh hash
/// and writes the new one in place.
/// </summary>
public sealed class SessionStore(PlatformDbContext db, IDedicatedConnectionFactory connections) : ISessionStore
{
    public async Task<Guid> CreateAsync(SessionCreate r, CancellationToken ct)
    {
        var sessionId = Guid.CreateVersion7();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform.user_sessions
                (session_id, user_id, token_hash, refresh_token_hash, active_tenant_id,
                 device_info, ip_address, issued_at, expires_at, refresh_expires_at, last_activity_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, CAST(@p6 AS inet), NOW(), @p7, @p8, NOW())
            """,
            Params(
                ("@p0", sessionId),
                ("@p1", r.UserId),
                ("@p2", r.AccessTokenHash),
                ("@p3", r.RefreshTokenHash),
                ("@p4", (object?)r.ActiveTenantId ?? DBNull.Value),
                ("@p5", (object?)r.DeviceInfo ?? DBNull.Value),
                ("@p6", (object?)r.IpAddress ?? DBNull.Value),
                ("@p7", r.ExpiresAtUtc),
                ("@p8", r.RefreshExpiresAtUtc)));
        return sessionId;
    }

    public Task<UserSessionRecord?> FindByRefreshHashAsync(string refreshTokenHash, CancellationToken ct) =>
        FindByRefreshHashIncludingRevokedAsync(refreshTokenHash, ct);

    public async Task<UserSessionRecord?> FindByRefreshHashIncludingRevokedAsync(string refreshTokenHash, CancellationToken ct)
    {
        // Returns the session whether or not it is revoked, so callers can detect reuse-after-revoke
        // (theft) by inspecting RevokedAtUtc.
        var rows = await db.Database
            .SqlQueryRaw<SessionRow>(
                """
                SELECT session_id AS "SessionId", user_id AS "UserId", active_tenant_id AS "ActiveTenantId",
                       refresh_expires_at AS "RefreshExpiresAtUtc", revoked_at AS "RevokedAtUtc"
                FROM platform.user_sessions
                WHERE refresh_token_hash = @p0
                ORDER BY issued_at DESC
                LIMIT 1
                """,
                new NpgsqlParameter("@p0", refreshTokenHash))
            .ToListAsync(ct);

        var row = rows.FirstOrDefault();
        return row is null
            ? null
            : new UserSessionRecord(row.SessionId, row.UserId, row.ActiveTenantId,
                row.RefreshExpiresAtUtc ?? DateTime.MinValue, row.RevokedAtUtc);
    }

    public Task RotateRefreshAsync(Guid sessionId, string newAccessHash, string newRefreshHash,
        DateTime newExpiresUtc, DateTime newRefreshExpiresUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE platform.user_sessions
            SET token_hash = @p1, refresh_token_hash = @p2, expires_at = @p3,
                refresh_expires_at = @p4, last_activity_at = NOW()
            WHERE session_id = @p0 AND revoked_at IS NULL
            """,
            Params(
                ("@p0", sessionId), ("@p1", newAccessHash), ("@p2", newRefreshHash),
                ("@p3", newExpiresUtc), ("@p4", newRefreshExpiresUtc)));

    public Task RotateRefreshWithTenantAsync(Guid sessionId, Guid? newActiveTenantId, string newAccessHash,
        string newRefreshHash, DateTime newExpiresUtc, DateTime newRefreshExpiresUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE platform.user_sessions
            SET active_tenant_id = @p5, token_hash = @p1, refresh_token_hash = @p2, expires_at = @p3,
                refresh_expires_at = @p4, last_activity_at = NOW()
            WHERE session_id = @p0 AND revoked_at IS NULL
            """,
            Params(
                ("@p0", sessionId), ("@p1", newAccessHash), ("@p2", newRefreshHash),
                ("@p3", newExpiresUtc), ("@p4", newRefreshExpiresUtc),
                ("@p5", (object?)newActiveTenantId ?? DBNull.Value)));

    public Task RevokeAsync(Guid sessionId, string reason, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE platform.user_sessions SET revoked_at = NOW(), revoked_reason = @p1 WHERE session_id = @p0 AND revoked_at IS NULL",
            Params(("@p0", sessionId), ("@p1", reason)));

    public Task RevokeByAccessHashAsync(string accessTokenHash, string reason, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE platform.user_sessions SET revoked_at = NOW(), revoked_reason = @p1 WHERE token_hash = @p0 AND revoked_at IS NULL",
            Params(("@p0", accessTokenHash), ("@p1", reason)));

    public async Task RevokeAllForUserAsync(Guid userId, string reason, CancellationToken ct)
    {
        // Theft-mitigation chain-revoke runs on a DEDICATED connection so it SURVIVES the refresh handler's
        // subsequent throw (the command transaction would otherwise roll the revoke back).
        await using var conn = await connections.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE platform.user_sessions SET revoked_at = NOW(), revoked_reason = @p1 WHERE user_id = @p0 AND revoked_at IS NULL", conn);
        cmd.Parameters.AddWithValue("@p0", userId);
        cmd.Parameters.AddWithValue("@p1", reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static object[] Params(params (string Name, object Value)[] ps) =>
        ps.Select(p => (object)new NpgsqlParameter(p.Name, p.Value)).ToArray();

    private sealed record SessionRow(
        Guid SessionId, Guid UserId, Guid? ActiveTenantId, DateTime? RefreshExpiresAtUtc, DateTime? RevokedAtUtc);
}
