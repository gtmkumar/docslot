namespace mediq.Application.Abstractions;

/// <summary>
/// Appends an action to the hash-chained <c>platform.audit_log</c>. NEVER deletes rows. The writer
/// computes the next chain hash from the previous row so the trail is tamper-evident
/// (database/05_security_hardening.sql owns <c>verify_audit_chain()</c>).
/// </summary>
public interface IAuditTrailWriter
{
    Task RecordAsync(AuditEntry entry, CancellationToken ct);
}

public sealed record AuditEntry(
    string Action,            // 'login', 'create', 'update', 'assign_role', 'grant_override', ...
    string ResourceType,      // 'user', 'user_tenant_role', 'session', ...
    Guid? ResourceId,
    string? ResourceLabel,
    Guid? UserId,
    Guid? TenantId,
    string? CorrelationId,
    string? IpAddress,
    string? UserAgent,
    bool Success,
    string? ChangeSummary = null,
    string? Purpose = null,
    string? LegalBasis = null);

/// <summary>
/// Backs the Idempotency-Key pipeline behavior. A money/booking POST carrying an Idempotency-Key is
/// captured here so a retry with the same key returns the first response instead of re-executing.
/// Wired now (slice 01); booking endpoints arrive in slice 03.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>Returns the cached response payload if this (tenant, endpoint, key) was already processed; null otherwise.</summary>
    Task<string?> TryGetAsync(Guid? tenantId, string endpoint, string idempotencyKey, CancellationToken ct);

    /// <summary>Persists the first successful response payload for this (tenant, endpoint, key). Durable.</summary>
    Task SaveAsync(Guid? tenantId, string endpoint, string idempotencyKey, string responsePayload, CancellationToken ct);
}

/// <summary>
/// Ambient holder for the current request's Idempotency-Key (set by the API from the header) and the
/// endpoint discriminator (so the same key on different endpoints doesn't collide). The pipeline behavior
/// reads these; absence of a key means the request is not idempotency-guarded.
/// </summary>
public interface IIdempotencyContext
{
    string? Key { get; }
    string Endpoint { get; }
}
