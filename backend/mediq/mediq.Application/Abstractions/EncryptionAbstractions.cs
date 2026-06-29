namespace mediq.Application.Abstractions;

/// <summary>
/// Key-management abstraction (KMS-backed in prod: GCP/Azure/AWS KMS or Vault; a local envelope provider
/// in dev). Key MATERIAL never lives in the DB — <c>platform.encryption_keys</c> stores only KMS references
/// and rotation metadata. The service resolves the active key for a (tenant, data_class), wraps/unwraps a
/// per-record data-encryption key (envelope encryption), and supports cryptographic erasure via
/// <see cref="DestroyKeyAsync"/> (the prerequisite for DPDP §12 right-to-erasure).
/// </summary>
public interface IKeyManagementService
{
    /// <summary>Resolves (or provisions) the active key reference for a tenant + data class.</summary>
    Task<KeyRef> GetActiveKeyAsync(Guid? tenantId, string dataClass, CancellationToken ct);

    /// <summary>Looks up a key by id (for decrypt with a possibly-rotated/deactivated key).</summary>
    Task<KeyRef?> GetKeyByIdAsync(Guid keyId, CancellationToken ct);

    /// <summary>Wraps a freshly-generated data key under the KMS key (returns the protected blob to store).</summary>
    byte[] WrapDataKey(KeyRef key, byte[] dataKey);

    /// <summary>Unwraps a stored data key. Throws if the key has been destroyed (cryptographic erasure).</summary>
    byte[] UnwrapDataKey(KeyRef key, byte[] wrappedDataKey);

    /// <summary>
    /// Cryptographically erases a key (DPDP §12): marks it destroyed and renders all ciphertext sealed
    /// under it unrecoverable. Ciphertext rows stay (FKs intact) but can never be decrypted again.
    /// </summary>
    Task DestroyKeyAsync(Guid keyId, Guid byUserId, CancellationToken ct);
}

/// <summary>A resolved KMS key reference + the metadata needed to wrap/unwrap. Holds NO raw key material persisted to the DB.</summary>
public sealed record KeyRef(Guid KeyId, Guid? TenantId, string DataClass, string KmsProvider, string KeyReference, int KeyVersion, bool IsDestroyed);

/// <summary>
/// Registry-driven field encryption (Layer 3). Encrypts/decrypts a field per the
/// <c>platform.encrypted_fields_registry</c> contract, logging every operation to
/// <c>platform.key_usage_log</c>. Produces a self-describing envelope so reads can resolve the right key.
/// </summary>
public interface IFieldEncryptionService
{
    /// <summary>Encrypts a plaintext field. Returns a base64 envelope string (key_id + iv + ciphertext + tag).</summary>
    Task<string> EncryptAsync(FieldRef field, Guid? tenantId, string plaintext, EncryptionContext ctx, CancellationToken ct);

    /// <summary>Decrypts an envelope produced by <see cref="EncryptAsync"/>. Throws if the key was destroyed.</summary>
    Task<string> DecryptAsync(FieldRef field, string envelope, EncryptionContext ctx, CancellationToken ct);

    /// <summary>Encrypts raw bytes (e.g. a PHI file blob) under the field's data_class key. Same envelope as
    /// <see cref="EncryptAsync"/>, so the same key resolution + DPDP cryptographic erasure apply.</summary>
    Task<string> EncryptBytesAsync(FieldRef field, Guid? tenantId, byte[] plaintext, EncryptionContext ctx, CancellationToken ct);

    /// <summary>Decrypts an envelope produced by <see cref="EncryptBytesAsync"/> back to raw bytes.</summary>
    Task<byte[]> DecryptBytesAsync(FieldRef field, string envelope, EncryptionContext ctx, CancellationToken ct);

    /// <summary>True if the registry marks (schema.table.column) as encryption-required.</summary>
    Task<bool> IsRegisteredAsync(FieldRef field, CancellationToken ct);
}

/// <summary>Identifies a registered field (schema.table.column → data_class lookup).</summary>
public sealed record FieldRef(string Schema, string Table, string Column);

/// <summary>Actor/resource context for key_usage_log forensics.</summary>
public sealed record EncryptionContext(Guid? UserId, Guid? TenantId, string? ResourceType, Guid? ResourceId, string? IpAddress);
