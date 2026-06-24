using mediq.Domain.Platform;

namespace mediq.Application.Abstractions;

/// <summary>Testable wall-clock. Never call <see cref="DateTime.UtcNow"/> directly in handlers.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>
/// The authenticated principal + active tenant for the current request, populated from the JWT by the
/// API layer. Application/audit code consumes this instead of reaching into <c>HttpContext</c>.
/// </summary>
public interface ICurrentUserContext
{
    Guid? UserId { get; }
    string? Email { get; }

    /// <summary>
    /// The tenant the request is scoped to — derived ONLY from the validated JWT <c>tenant_id</c> claim
    /// (never a client header). This is the value used for RLS (<c>app.tenant_id</c>) and permission
    /// resolution, so it must be server-signed. Switching tenant re-issues a token via
    /// <c>POST /api/v1/auth/switch-tenant</c> after a server-side membership check.
    /// </summary>
    Guid? TenantId { get; }

    /// <summary>Correlation id flowing across HTTP and (later) RabbitMQ boundaries.</summary>
    string? CorrelationId { get; }

    string? IpAddress { get; }
    string? UserAgent { get; }
    bool IsAuthenticated { get; }

    /// <summary>
    /// The caller's OWN broker identity, from the server-signed <c>broker_id</c> JWT claim (present only for
    /// broker-role users). Broker self-service endpoints MUST use this — never a client-supplied id — so a
    /// broker can only ever reach their own wallet/links (IDOR-safe).
    /// </summary>
    Guid? BrokerId { get; }
}

/// <summary>Commit boundary. The UnitOfWork command behavior calls this once per command.</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens a database transaction and applies <c>SET LOCAL app.tenant_id</c> (plus <c>app.is_super_admin</c>,
    /// derived from the validated user) within it, so PostgreSQL RLS policies scope rows to the active tenant —
    /// and admit a platform super_admin cross-tenant/globally — ONLY for the lifetime of that transaction. The
    /// returned scope MUST be disposed (commit/rollback ends the tx → the GUCs auto-clear → no pool bleed). This
    /// is the pool-safe replacement for the old session-scoped GUC. No tenant context = a tx with no GUC.
    /// </summary>
    Task<ITenantScope> BeginTenantScopeAsync(Guid? tenantId, CancellationToken ct = default);
}

/// <summary>
/// A transaction-scoped tenant-RLS context. Disposing ends the transaction — which clears
/// <c>SET LOCAL app.tenant_id</c> so it can never leak onto a pooled connection reused by another request.
/// </summary>
public interface ITenantScope : IAsyncDisposable
{
    /// <summary>Commits the underlying transaction (command path). Read paths just dispose without committing.</summary>
    Task CommitAsync(CancellationToken ct = default);
}

/// <summary>
/// The resolve-once-per-request permission cache (NFR-PERF-01). The authorization middleware resolves the
/// effective set exactly once via <c>platform.resolve_user_permissions()</c> and stores it here; all
/// in-memory <c>Has()</c> checks read from it. Never call the DB per permission check.
/// </summary>
public interface IPermissionContext
{
    bool IsResolved { get; }
    IReadOnlySet<string> Keys { get; }
    void Set(IReadOnlySet<string> keys);
    bool Has(string permissionKey);
}
