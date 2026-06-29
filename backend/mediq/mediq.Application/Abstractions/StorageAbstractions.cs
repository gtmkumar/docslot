namespace mediq.Application.Abstractions;

/// <summary>
/// Tenant-scoped blob storage for PHI artifacts (lab-report PDFs/images). The bytes handed to
/// <see cref="PutAsync"/> are ALREADY an encrypted envelope (the application encrypts before storing), so an
/// adapter only ever holds opaque ciphertext. Dev adapters store on the local filesystem or in memory; prod
/// swaps in an object store (S3/GCS/Azure + provider SSE/KMS) behind this same interface. Every key is
/// namespaced by tenant_id, and <see cref="GetAsync"/> refuses a key outside the caller's tenant namespace —
/// defense-in-depth on top of the fact that the DB only hands a caller a storage key for a row RLS lets them read.
/// </summary>
public interface IBlobStorage
{
    /// <summary>Stores bytes under a NEW tenant-namespaced key; returns the opaque key + byte length.</summary>
    Task<StoredBlobRef> PutAsync(Guid tenantId, string resourceType, Guid resourceId, string fileName, byte[] content, CancellationToken ct);

    /// <summary>Reads bytes for a tenant-namespaced key. Returns null if absent OR the key is not within the
    /// caller's tenant namespace (cross-tenant guard).</summary>
    Task<byte[]?> GetAsync(string storageKey, Guid tenantId, CancellationToken ct);
}

/// <summary>A stored blob's opaque key + size (the key is persisted on the owning row, e.g. lab_reports.file_url).</summary>
public sealed record StoredBlobRef(string StorageKey, long SizeBytes);
