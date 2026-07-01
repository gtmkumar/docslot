using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Tenant IP allow-list over the existing <c>platform.ip_allowlist</c> table (issue #91 REUSES it — no new
/// DDL). Entries are tenant-wide (<c>user_id IS NULL</c>). Containment uses PostgreSQL's <c>&gt;&gt;=</c> (a CIDR
/// contains-or-equals an inet). DELETE is a soft-deactivate because <c>docslot_app</c> has no DELETE grant on
/// platform tables. platform.ip_allowlist has no RLS → every statement is scoped by <c>tenant_id</c>.
/// </summary>
public sealed class IpAllowlistService(PlatformDbContext db) : IIpAllowlistService
{
    public async Task<IReadOnlyList<IpAllowlistEntryDto>> ListAsync(Guid tenantId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<AllowRow>(
                """
                SELECT allowlist_id AS "AllowlistId", host(cidr_range) || '/' || masklen(cidr_range) AS "CidrRange",
                       label AS "Label", is_active AS "IsActive", created_at AS "CreatedAt", expires_at AS "ExpiresAt"
                FROM platform.ip_allowlist
                WHERE tenant_id = @p0
                ORDER BY created_at DESC
                """,
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);

        return rows.Select(r => new IpAllowlistEntryDto(
            r.AllowlistId, r.CidrRange, r.Label, r.IsActive, r.CreatedAt, r.ExpiresAt)).ToList();
    }

    public async Task<Guid> AddAsync(
        Guid tenantId, Guid createdByUserId, string cidrRange, string? label, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<IdRow>(
                """
                INSERT INTO platform.ip_allowlist
                    (tenant_id, user_id, cidr_range, label, is_active, created_by_user_id, expires_at)
                VALUES (@p0, NULL, CAST(@p1 AS cidr), @p2, true, @p3, @p4)
                RETURNING allowlist_id AS "Value"
                """,
                new NpgsqlParameter("@p0", tenantId),
                new NpgsqlParameter("@p1", cidrRange),
                new NpgsqlParameter("@p2", (object?)label ?? DBNull.Value),
                new NpgsqlParameter("@p3", createdByUserId),
                new NpgsqlParameter("@p4", (object?)expiresAt ?? DBNull.Value))
            .ToListAsync(ct);

        return rows.First().Value;
    }

    public async Task<bool> DeactivateAsync(Guid tenantId, Guid allowlistId, CancellationToken ct)
    {
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE platform.ip_allowlist SET is_active = false
            WHERE allowlist_id = @p1 AND tenant_id = @p0 AND is_active = true
            """,
            [new NpgsqlParameter("@p0", tenantId), new NpgsqlParameter("@p1", allowlistId)], ct);
        return affected > 0;
    }

    public async Task<bool> IsIpAllowedAsync(Guid tenantId, string? ipAddress, CancellationToken ct)
    {
        // No parseable source IP under an enforced allow-list → fail closed (cannot prove the caller is inside it).
        if (string.IsNullOrWhiteSpace(ipAddress) || !System.Net.IPAddress.TryParse(ipAddress, out _))
            return false;

        var rows = await db.Database.SqlQueryRaw<BoolRow>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM platform.ip_allowlist
                    WHERE tenant_id = @p0 AND is_active = true
                      AND (expires_at IS NULL OR expires_at > NOW())
                      AND cidr_range >>= CAST(@p1 AS inet)
                ) AS "Value"
                """,
                new NpgsqlParameter("@p0", tenantId), new NpgsqlParameter("@p1", ipAddress))
            .ToListAsync(ct);

        return rows.FirstOrDefault()?.Value ?? false;
    }

    private sealed record AllowRow(
        Guid AllowlistId, string CidrRange, string? Label, bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);
    private sealed record IdRow(Guid Value);
    private sealed record BoolRow(bool Value);
}
