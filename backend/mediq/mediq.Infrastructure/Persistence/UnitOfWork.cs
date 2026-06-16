using mediq.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace mediq.Infrastructure.Persistence;

/// <summary>
/// Commit boundary over <see cref="PlatformDbContext"/>. <see cref="BeginTenantScopeAsync"/> opens a
/// transaction and issues <c>SET LOCAL app.tenant_id</c> inside it, so PostgreSQL RLS policies
/// (database/05_security_hardening.sql) scope rows to the active tenant for the lifetime of THAT transaction
/// only. When the scope is disposed (commit on writes, plain dispose on reads) the transaction ends and the
/// LOCAL GUC is discarded — so the tenant scope can NEVER bleed onto a pooled connection reused by another
/// request (the prior session-scoped, never-reset GUC was a cross-tenant RLS hazard).
/// </summary>
public sealed class UnitOfWork(PlatformDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    public async Task<ITenantScope> BeginTenantScopeAsync(Guid? tenantId, CancellationToken ct = default)
    {
        // Reuse the ambient EF transaction if one already exists (e.g. nested call within a command), so we
        // don't try to open a second transaction on the same context.
        var existing = db.Database.CurrentTransaction;
        var tx = existing ?? await db.Database.BeginTransactionAsync(ct);

        // SET LOCAL (is_local=true) — scoped to THIS transaction; auto-clears on commit/rollback.
        await db.Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.tenant_id', @p0, true)",
            new[] { new NpgsqlParameter("@p0", tenantId?.ToString() ?? "") }, ct);

        return new TenantScope(tx, ownsTransaction: existing is null);
    }

    /// <summary>Owns the transaction only when it opened one (not when reusing an ambient command tx).</summary>
    private sealed class TenantScope(IDbContextTransaction tx, bool ownsTransaction) : ITenantScope
    {
        private bool _committed;

        public async Task CommitAsync(CancellationToken ct = default)
        {
            if (ownsTransaction)
            {
                await tx.CommitAsync(ct);
                _committed = true;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (ownsTransaction)
            {
                // A read scope (or a failed command) disposes without committing → the tx rolls back and the
                // SET LOCAL app.tenant_id is discarded. Disposing an already-committed tx is a safe no-op.
                if (!_committed)
                {
                    try { await tx.RollbackAsync(); } catch { /* tx may already be completed */ }
                }
                await tx.DisposeAsync();
            }
        }
    }
}
