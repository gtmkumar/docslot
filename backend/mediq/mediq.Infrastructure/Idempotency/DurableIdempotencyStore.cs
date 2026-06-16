using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Idempotency;

/// <summary>
/// Durable, table-backed idempotency store (replaces the slice-01 in-memory cache). Keyed by
/// (tenant_scope, endpoint, idempotency_key) with a UNIQUE constraint, so a retried booking/money POST can
/// never double-execute across restart OR scale-out — the second writer hits the unique constraint and the
/// reader returns the first stored response. Backed by the app-owned <c>platform.idempotency_keys</c> table.
/// </summary>
public sealed class DurableIdempotencyStore(PlatformDbContext db) : IIdempotencyStore
{
    public async Task<string?> TryGetAsync(Guid? tenantId, string endpoint, string idempotencyKey, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<PayloadRow>(
                """
                SELECT response_payload AS "Payload"
                FROM platform.idempotency_keys
                WHERE tenant_scope = @p0 AND endpoint = @p1 AND idempotency_key = @p2
                LIMIT 1
                """,
                new NpgsqlParameter("@p0", Scope(tenantId)),
                new NpgsqlParameter("@p1", endpoint),
                new NpgsqlParameter("@p2", idempotencyKey))
            .ToListAsync(ct);
        return rows.FirstOrDefault()?.Payload;
    }

    public async Task SaveAsync(Guid? tenantId, string endpoint, string idempotencyKey, string responsePayload, CancellationToken ct)
    {
        // ON CONFLICT DO NOTHING: if a concurrent/restarted request already stored a response for this key,
        // the first one wins and this is a no-op — the de-dup is enforced at the DB level.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform.idempotency_keys
                (idempotency_id, tenant_scope, endpoint, idempotency_key, response_payload, created_at)
            VALUES (gen_random_uuid(), @p0, @p1, @p2, @p3, NOW())
            ON CONFLICT (tenant_scope, endpoint, idempotency_key) DO NOTHING
            """,
            new NpgsqlParameter("@p0", Scope(tenantId)),
            new NpgsqlParameter("@p1", endpoint),
            new NpgsqlParameter("@p2", idempotencyKey),
            new NpgsqlParameter("@p3", responsePayload));
    }

    private static string Scope(Guid? tenantId) => tenantId?.ToString() ?? "platform";

    private sealed record PayloadRow(string Payload);
}
