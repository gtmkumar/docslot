using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.PlatformApi;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.PlatformApi;

/// <summary>Reads the <c>platform_api.api_scopes</c> registry.</summary>
public sealed class ApiScopeRepository(PlatformDbContext db) : IApiScopeRepository
{
    public async Task<IReadOnlyList<ScopeDto>> ListAsync(CancellationToken ct) =>
        await db.ApiScopes.AsNoTracking()
            .OrderBy(s => s.ScopeKey)
            .Select(s => new ScopeDto(s.ScopeKey, s.Resource, s.Action, s.Description, s.IsDangerous, s.RequiresConsent))
            .ToListAsync(ct);

    public async Task<IReadOnlySet<string>> ExistingScopeKeysAsync(IReadOnlyCollection<string> candidates, CancellationToken ct)
    {
        var list = candidates.ToList();
        var found = await db.ApiScopes.AsNoTracking()
            .Where(s => list.Contains(s.ScopeKey))
            .Select(s => s.ScopeKey)
            .ToListAsync(ct);
        return found.ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// Persists/revokes issued client tokens (<c>platform_api.api_tokens</c>). Only the SHA-256 token HASH is
/// stored. Lookups go through the canonical <c>platform_api.token_has_scope</c>-equivalent logic but return
/// the full granted-scope set so the API can resolve-once in memory.
/// </summary>
public sealed class ApiTokenStore(PlatformDbContext db) : IApiTokenStore
{
    public Task CreateAsync(Guid clientId, string tokenHash, IReadOnlyCollection<string> requested,
        IReadOnlyCollection<string> granted, Guid? tenantId, DateTime expiresUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform_api.api_tokens
                (token_id, client_id, token_hash, requested_scopes, granted_scopes, tenant_id, issued_at, expires_at)
            VALUES (gen_random_uuid(), @p0, @p1, @p2, @p3, @p4, NOW(), @p5)
            """,
            new NpgsqlParameter("@p0", clientId),
            new NpgsqlParameter("@p1", tokenHash),
            new NpgsqlParameter("@p2", requested.ToArray()),
            new NpgsqlParameter("@p3", granted.ToArray()),
            new NpgsqlParameter("@p4", (object?)tenantId ?? DBNull.Value),
            new NpgsqlParameter("@p5", expiresUtc));

    public async Task<ApiTokenLookup?> FindLiveByHashAsync(string tokenHash, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<TokenRow>(
                """
                SELECT token_id AS "TokenId", client_id AS "ClientId", tenant_id AS "TenantId",
                       granted_scopes AS "GrantedScopes"
                FROM platform_api.api_tokens
                WHERE token_hash = @p0 AND revoked_at IS NULL AND expires_at > NOW()
                LIMIT 1
                """,
                new NpgsqlParameter("@p0", tokenHash))
            .ToListAsync(ct);

        var row = rows.FirstOrDefault();
        return row is null
            ? null
            : new ApiTokenLookup(row.TokenId, row.ClientId, row.TenantId,
                row.GrantedScopes.ToHashSet(StringComparer.Ordinal));
    }

    public Task RevokeByHashAsync(string tokenHash, string reason, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE platform_api.api_tokens SET revoked_at = NOW(), revoked_reason = @p1 WHERE token_hash = @p0 AND revoked_at IS NULL",
            new NpgsqlParameter("@p0", tokenHash), new NpgsqlParameter("@p1", reason));

    private sealed record TokenRow(Guid TokenId, Guid ClientId, Guid? TenantId, string[] GrantedScopes);
}

/// <summary>Appends to <c>platform_api.api_requests</c> and counts recent requests for rate limiting.</summary>
public sealed class ApiRequestLogWriter(PlatformDbContext db) : IApiRequestLogWriter
{
    public Task RecordAsync(ApiRequestLogEntry e, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform_api.api_requests
                (request_id, client_id, token_id, tenant_id, method, path, ip_address, user_agent,
                 status_code, response_time_ms, error_code, occurred_at)
            VALUES (gen_random_uuid(), @p0, @p1, @p2, @p3, @p4, CAST(@p5 AS inet), @p6, @p7, @p8, @p9, NOW())
            """,
            new NpgsqlParameter("@p0", (object?)e.ClientId ?? DBNull.Value),
            new NpgsqlParameter("@p1", (object?)e.TokenId ?? DBNull.Value),
            new NpgsqlParameter("@p2", (object?)e.TenantId ?? DBNull.Value),
            new NpgsqlParameter("@p3", e.Method),
            new NpgsqlParameter("@p4", e.Path),
            new NpgsqlParameter("@p5", (object?)e.IpAddress ?? DBNull.Value),
            new NpgsqlParameter("@p6", (object?)e.UserAgent ?? DBNull.Value),
            new NpgsqlParameter("@p7", e.StatusCode),
            new NpgsqlParameter("@p8", (object?)e.ResponseTimeMs ?? DBNull.Value),
            new NpgsqlParameter("@p9", (object?)e.ErrorCode ?? DBNull.Value));

    public async Task<int> CountRecentAsync(Guid clientId, TimeSpan window, CancellationToken ct)
    {
        var since = DateTime.UtcNow - window;
        var rows = await db.Database.SqlQueryRaw<CountRow>(
                "SELECT COUNT(*)::int AS \"Count\" FROM platform_api.api_requests WHERE client_id = @p0 AND occurred_at >= @p1",
                new NpgsqlParameter("@p0", clientId), new NpgsqlParameter("@p1", since))
            .ToListAsync(ct);
        return rows.FirstOrDefault()?.Count ?? 0;
    }

    public async Task<(int Minute, int Day)> CountWindowsAsync(Guid clientId, DateTime minuteSinceUtc, DateTime daySinceUtc, CancellationToken ct)
    {
        // One index range-scan over the trailing DAY; the FILTER derives the trailing-MINUTE count from it.
        var rows = await db.Database.SqlQueryRaw<WindowCountRow>(
                """
                SELECT COUNT(*) FILTER (WHERE occurred_at >= @p1)::int AS "Minute",
                       COUNT(*)::int AS "Day"
                FROM platform_api.api_requests
                WHERE client_id = @p0 AND occurred_at >= @p2
                """,
                new NpgsqlParameter("@p0", clientId),
                new NpgsqlParameter("@p1", minuteSinceUtc),
                new NpgsqlParameter("@p2", daySinceUtc))
            .ToListAsync(ct);
        var r = rows.FirstOrDefault();
        return (r?.Minute ?? 0, r?.Day ?? 0);
    }

    private sealed record CountRow(int Count);
    private sealed record WindowCountRow(int Minute, int Day);
}

/// <summary>
/// Reads <c>platform_api.api_requests</c> for the developers/Logs surface. Projects metadata ONLY
/// (method/path/status/latency + client name + the granted scope from the token). NEVER selects
/// ip_address, user_agent, error_message bodies, or any PHI. Offset-paginated with a total count.
/// </summary>
public sealed class ApiRequestLogReader(PlatformDbContext db) : IApiRequestLogReader
{
    public async Task<ApiRequestLogPage> ListAsync(ApiRequestLogFilter f, CancellationToken ct)
    {
        var offset = (f.Page - 1) * f.PageSize;

        // The token's granted_scopes is a text[]; surface the first as the "scope used" for the row.
        var rows = await db.Database.SqlQueryRaw<LogRow>(
                """
                SELECT r.request_id AS "RequestId", r.client_id AS "ClientId", c.client_name AS "ClientName",
                       r.method AS "Method", r.path AS "Path",
                       (SELECT t.granted_scopes[1] FROM platform_api.api_tokens t WHERE t.token_id = r.token_id) AS "ScopeUsed",
                       r.status_code AS "StatusCode", r.response_time_ms AS "ResponseTimeMs", r.occurred_at AS "OccurredAt"
                FROM platform_api.api_requests r
                LEFT JOIN platform_api.api_clients c ON c.client_id = r.client_id
                WHERE (@p0::uuid IS NULL OR r.client_id = @p0::uuid)
                  AND (@p1::timestamptz IS NULL OR r.occurred_at >= @p1::timestamptz)
                  AND (@p2::timestamptz IS NULL OR r.occurred_at <= @p2::timestamptz)
                ORDER BY r.occurred_at DESC
                OFFSET @p3 LIMIT @p4
                """,
                P(("@p0", (object?)f.ClientId ?? DBNull.Value),
                  ("@p1", (object?)f.From?.UtcDateTime ?? DBNull.Value),
                  ("@p2", (object?)f.To?.UtcDateTime ?? DBNull.Value),
                  ("@p3", offset), ("@p4", f.PageSize)))
            .ToListAsync(ct);

        var totals = await db.Database.SqlQueryRaw<TotalRow>(
                """
                SELECT COUNT(*)::int AS "Total" FROM platform_api.api_requests r
                WHERE (@p0::uuid IS NULL OR r.client_id = @p0::uuid)
                  AND (@p1::timestamptz IS NULL OR r.occurred_at >= @p1::timestamptz)
                  AND (@p2::timestamptz IS NULL OR r.occurred_at <= @p2::timestamptz)
                """,
                P(("@p0", (object?)f.ClientId ?? DBNull.Value),
                  ("@p1", (object?)f.From?.UtcDateTime ?? DBNull.Value),
                  ("@p2", (object?)f.To?.UtcDateTime ?? DBNull.Value)))
            .ToListAsync(ct);

        var items = rows.Select(r => new ApiRequestLogRow(
            r.RequestId, r.ClientId, r.ClientName, r.Method, r.Path, r.ScopeUsed, r.StatusCode, r.ResponseTimeMs, r.OccurredAt)).ToList();
        return new ApiRequestLogPage(items, totals.FirstOrDefault()?.Total ?? 0, f.Page, f.PageSize);
    }

    private static object[] P(params (string Name, object Value)[] ps) =>
        ps.Select(p => (object)new NpgsqlParameter(p.Name, p.Value)).ToArray();

    private sealed record LogRow(
        Guid RequestId, Guid? ClientId, string? ClientName, string Method, string Path,
        string? ScopeUsed, int StatusCode, int? ResponseTimeMs, DateTime OccurredAt);
    private sealed record TotalRow(int Total);
}
