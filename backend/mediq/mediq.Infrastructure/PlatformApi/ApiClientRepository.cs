using mediq.Application.Abstractions;
using mediq.Domain.PlatformApi;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.PlatformApi;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.PlatformApi;

/// <summary>
/// Reads/writes <c>platform_api.api_clients</c> + <c>api_client_scopes</c>. Writes use parameterized raw
/// SQL (the schema owns defaults/triggers); the secret is only ever stored as a hash.
/// </summary>
public sealed class ApiClientRepository(PlatformDbContext db) : IApiClientRepository
{
    public Task<ApiClient?> GetByCodeAsync(string clientCode, CancellationToken ct) =>
        db.ApiClients.FirstOrDefaultAsync(c => c.ClientCode == clientCode && c.DeletedAt == null, ct);

    public Task<ApiClient?> GetByIdAsync(Guid clientId, CancellationToken ct) =>
        db.ApiClients.FirstOrDefaultAsync(c => c.ClientId == clientId && c.DeletedAt == null, ct);

    public async Task<IReadOnlyList<ApiClientDto>> ListAsync(int skip, int take, CancellationToken ct)
    {
        var clients = await db.ApiClients.AsNoTracking()
            .Where(c => c.DeletedAt == null)
            .OrderBy(c => c.ClientName)
            .Skip(skip).Take(take)
            .ToListAsync(ct);

        var ids = clients.Select(c => c.ClientId).ToList();
        var scopeMap = await (
            from cs in db.ApiClientScopes.AsNoTracking()
            join s in db.ApiScopes.AsNoTracking() on cs.ScopeId equals s.ScopeId
            where ids.Contains(cs.ClientId)
            select new { cs.ClientId, s.ScopeKey })
            .ToListAsync(ct);

        var byClient = scopeMap.GroupBy(x => x.ClientId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ScopeKey).ToList());

        return clients.Select(c => new ApiClientDto(
            c.ClientId, c.ClientCode, c.ClientName, c.ClientType, c.OwnerTenantId, c.OwnerEmail,
            c.OwnerOrganization, c.IsActive, c.IsVerified, c.RateLimitPerMinute, c.RateLimitPerDay,
            c.BurstLimit, byClient.GetValueOrDefault(c.ClientId, []), c.CreatedAt, c.LastUsedAt)).ToList();
    }

    public async Task<IReadOnlySet<string>> GetGrantedScopeKeysAsync(Guid clientId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var keys = await (
            from cs in db.ApiClientScopes.AsNoTracking()
            join s in db.ApiScopes.AsNoTracking() on cs.ScopeId equals s.ScopeId
            where cs.ClientId == clientId && (cs.ExpiresAt == null || cs.ExpiresAt > now)
            select s.ScopeKey).ToListAsync(ct);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<Guid> CreateAsync(RegisterApiClientRequest req, string secretHash, DateTime nowUtc, CancellationToken ct)
    {
        var clientId = Guid.CreateVersion7();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform_api.api_clients
                (client_id, client_code, client_name, client_secret_hash, client_type, owner_tenant_id,
                 owner_email, owner_organization, purpose, is_active, is_verified, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, false, false, @p9, @p9)
            """,
            P(("@p0", clientId), ("@p1", req.ClientCode), ("@p2", req.ClientName), ("@p3", secretHash),
              ("@p4", req.ClientType), ("@p5", (object?)req.OwnerTenantId ?? DBNull.Value),
              ("@p6", req.OwnerEmail), ("@p7", (object?)req.OwnerOrganization ?? DBNull.Value),
              ("@p8", req.Purpose), ("@p9", nowUtc)));
        return clientId;
    }

    public Task UpdateSecretHashAsync(Guid clientId, string secretHash, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE platform_api.api_clients SET client_secret_hash = @p1 WHERE client_id = @p0",
            P(("@p0", clientId), ("@p1", secretHash)));

    public Task SetStatusAsync(Guid clientId, bool isActive, bool isVerified, Guid? verifiedBy, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE platform_api.api_clients
            SET is_active = @p1, is_verified = @p2,
                verified_at = CASE WHEN @p2 THEN @p4 ELSE verified_at END,
                verified_by = CASE WHEN @p2 THEN @p3 ELSE verified_by END
            WHERE client_id = @p0
            """,
            P(("@p0", clientId), ("@p1", isActive), ("@p2", isVerified),
              ("@p3", (object?)verifiedBy ?? DBNull.Value), ("@p4", nowUtc)));

    public Task SetRateLimitsAsync(Guid clientId, int perMinute, int perDay, int burst, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE platform_api.api_clients SET rate_limit_per_minute = @p1, rate_limit_per_day = @p2, burst_limit = @p3 WHERE client_id = @p0",
            P(("@p0", clientId), ("@p1", perMinute), ("@p2", perDay), ("@p3", burst)));

    public async Task SetScopesAsync(Guid clientId, IReadOnlyList<string> scopeKeys, Guid? grantedBy, DateTime nowUtc, CancellationToken ct)
    {
        // Replace the grant set atomically: delete existing, insert the new keys (resolved to scope_ids).
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM platform_api.api_client_scopes WHERE client_id = @p0", P(("@p0", clientId)));

        foreach (var key in scopeKeys.Distinct(StringComparer.Ordinal))
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO platform_api.api_client_scopes (client_id, scope_id, granted_at, granted_by)
                SELECT @p0, s.scope_id, @p2, @p3 FROM platform_api.api_scopes s WHERE s.scope_key = @p1
                ON CONFLICT (client_id, scope_id) DO NOTHING
                """,
                P(("@p0", clientId), ("@p1", key), ("@p2", nowUtc), ("@p3", (object?)grantedBy ?? DBNull.Value)));
    }

    public Task TouchLastUsedAsync(Guid clientId, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE platform_api.api_clients SET last_used_at = @p1 WHERE client_id = @p0",
            P(("@p0", clientId), ("@p1", nowUtc)));

    private static object[] P(params (string Name, object Value)[] ps) =>
        ps.Select(p => (object)new NpgsqlParameter(p.Name, p.Value)).ToArray();
}
