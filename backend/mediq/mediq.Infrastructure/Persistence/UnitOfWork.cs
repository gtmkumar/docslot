using mediq.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace mediq.Infrastructure.Persistence;

/// <summary>
/// Commit boundary over <see cref="PlatformDbContext"/>. <see cref="BeginTenantScopeAsync"/> opens a
/// transaction and issues <c>SET LOCAL app.tenant_id</c> inside it, so PostgreSQL RLS policies
/// (database/05_security_hardening.sql, database/11_rbac_hardening.sql) scope rows to the active tenant for
/// the lifetime of THAT transaction only. When the scope is disposed (commit on writes, plain dispose on
/// reads) the transaction ends and the LOCAL GUCs are discarded — so the scope can NEVER bleed onto a pooled
/// connection reused by another request (the prior session-scoped, never-reset GUC was a cross-tenant RLS
/// hazard).
/// <para>
/// Alongside the tenant scope it also sets <c>app.is_super_admin</c> (read by
/// <c>platform.current_is_super_admin()</c>). This is the app half of audit Finding 1: the R1 RBAC <c>*_write</c>
/// and the cross-tenant/global read predicates (<c>platform.rls_can_*_tenant</c>) only admit a platform
/// super_admin when this GUC is true. The value is derived AUTHORITATIVELY from the DB
/// (<c>platform.is_super_admin(user)</c>) for the validated <see cref="ICurrentUserContext.UserId"/> — never a
/// client/JWT-supplied flag (the JWT carries none), so it cannot be spoofed — and is computed in the SAME
/// round-trip as <c>app.tenant_id</c>. Unauthenticated / background contexts have a null UserId →
/// <c>is_super_admin(NULL)</c> → false (no privilege). The four RBAC admin writes still go through the SECURITY
/// DEFINER functions (which enforce the escalation guard); this GUC covers the non-definer paths.
/// </para>
/// <para>
/// It also sets <c>app.user_id</c> (the validated acting user) and <c>app.impersonated_tenant</c> (the
/// server-signed impersonation claim) for the same transaction — the inputs to the issue #3 PHI guard
/// <c>platform.current_impersonated_tenant()</c>. That guard only opens cross-tenant PHI when a live,
/// non-expired <c>impersonation_sessions</c> row (created solely by the audited <c>begin_impersonation()</c>)
/// backs the GUC for this user, so a forged or stale <c>app.impersonated_tenant</c> reaches no medical data.
/// </para>
/// </summary>
public sealed class UnitOfWork(PlatformDbContext db, ICurrentUserContext currentUser) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    public async Task CreateSavepointAsync(string name, CancellationToken ct = default)
    {
        var tx = db.Database.CurrentTransaction;
        if (tx is not null) await tx.CreateSavepointAsync(name, ct);
    }

    public async Task RollbackToSavepointAsync(string name, CancellationToken ct = default)
    {
        var tx = db.Database.CurrentTransaction;
        if (tx is not null) await tx.RollbackToSavepointAsync(name, ct);
    }

    public async Task<ITenantScope> BeginTenantScopeAsync(Guid? tenantId, CancellationToken ct = default)
    {
        // Reuse the ambient EF transaction if one already exists (e.g. nested call within a command), so we
        // don't try to open a second transaction on the same context.
        var existing = db.Database.CurrentTransaction;
        var tx = existing ?? await db.Database.BeginTransactionAsync(ct);

        // SET LOCAL (is_local=true) — scoped to THIS transaction; auto-clears on commit/rollback. The
        // super-admin flag is resolved server-side from the validated user id (NULL/anon ⇒ false).
        // app.user_id is the validated acting user; it is the identity the impersonation guard
        // (platform.current_impersonated_tenant) matches the session against — without it, app.impersonated_tenant
        // is inert (issue #3). app.impersonated_tenant carries the server-signed impersonation claim, but remains
        // AUDITED-BY-CONSTRUCTION: the DB ignores it unless a live begin_impersonation() session backs it for this
        // user. Empty string (not NULL) for absent values so current_setting()+NULLIF reads back as NULL.
        await db.Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.tenant_id', @p0, true), "
            + "set_config('app.user_id', @p1, true), "
            + "set_config('app.impersonated_tenant', @p2, true), "
            + "set_config('app.is_super_admin', platform.is_super_admin(@p3)::text, true)",
            new[]
            {
                new NpgsqlParameter("@p0", tenantId?.ToString() ?? ""),
                new NpgsqlParameter("@p1", currentUser.UserId?.ToString() ?? ""),
                new NpgsqlParameter("@p2", currentUser.ImpersonatedTenantId?.ToString() ?? ""),
                new NpgsqlParameter("@p3", NpgsqlDbType.Uuid) { Value = (object?)currentUser.UserId ?? DBNull.Value },
            }, ct);

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
