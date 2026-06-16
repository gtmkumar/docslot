using System.Security.Cryptography;
using System.Text;
using mediq.Application.Abstractions;
using mediq.Application.Options;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Local/dev envelope-encryption key manager (kms_provider = 'local_dev'). Wraps per-record data keys under
/// a MASTER key derived from a config/secret passphrase — the master key material NEVER touches the DB
/// (<c>platform.encryption_keys</c> stores only a logical key_reference + metadata). In prod this is
/// swapped for a GCP/Azure/AWS KMS implementation (same interface). Cryptographic erasure = mark the key
/// row 'destroyed' AND drop its wrapping salt so wrapped data keys can no longer be unwrapped.
/// </summary>
public sealed class LocalEnvelopeKeyManagementService(
    PlatformDbContext db, IOptions<EncryptionOptions> encryption, IClock clock) : IKeyManagementService
{
    private readonly string _masterPassphrase = encryption.Value.Passphrase;

    public async Task<KeyRef> GetActiveKeyAsync(Guid? tenantId, string dataClass, CancellationToken ct)
    {
        var existing = await FindActiveAsync(tenantId, dataClass, ct);
        if (existing is not null) return existing;

        // Provision a new local_dev key reference (a per-key salt embedded in key_reference; NO key material).
        var keyId = Guid.CreateVersion7();
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var keyReference = $"local-dev://{keyId}#{salt}";
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform.encryption_keys
                (key_id, tenant_id, data_class, key_reference, kms_provider, key_algorithm, key_version, status, activated_at, created_at)
            VALUES (@p0, @p1, @p2, @p3, 'local_dev', 'AES_256_GCM', 1, 'active', NOW(), NOW())
            """,
            new NpgsqlParameter("@p0", keyId),
            new NpgsqlParameter("@p1", (object?)tenantId ?? DBNull.Value),
            new NpgsqlParameter("@p2", dataClass),
            new NpgsqlParameter("@p3", keyReference));

        return new KeyRef(keyId, tenantId, dataClass, "local_dev", keyReference, 1, IsDestroyed: false);
    }

    public async Task<KeyRef?> GetKeyByIdAsync(Guid keyId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<KeyRow>(
                """
                SELECT key_id AS "KeyId", tenant_id AS "TenantId", data_class AS "DataClass",
                       kms_provider AS "KmsProvider", key_reference AS "KeyReference", key_version AS "KeyVersion",
                       (status = 'destroyed' OR destroyed_at IS NOT NULL) AS "IsDestroyed"
                FROM platform.encryption_keys WHERE key_id = @p0
                """,
                new NpgsqlParameter("@p0", keyId))
            .ToListAsync(ct);
        var r = rows.FirstOrDefault();
        return r is null ? null : new KeyRef(r.KeyId, r.TenantId, r.DataClass, r.KmsProvider, r.KeyReference, r.KeyVersion, r.IsDestroyed);
    }

    public byte[] WrapDataKey(KeyRef key, byte[] dataKey)
    {
        // AES-GCM wrap of the data key under the master KEK derived from passphrase + the key's salt.
        var (kek, _) = DeriveKek(key);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[dataKey.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(kek, 16);
        aes.Encrypt(nonce, dataKey, ciphertext, tag);
        return Concat(nonce, tag, ciphertext);
    }

    public byte[] UnwrapDataKey(KeyRef key, byte[] wrappedDataKey)
    {
        if (key.IsDestroyed)
            throw new InvalidOperationException($"Key {key.KeyId} has been cryptographically destroyed; data is unrecoverable.");

        var (kek, _) = DeriveKek(key);
        var nonce = wrappedDataKey[..12];
        var tag = wrappedDataKey[12..28];
        var ciphertext = wrappedDataKey[28..];
        var dataKey = new byte[ciphertext.Length];
        using var aes = new AesGcm(kek, 16);
        aes.Decrypt(nonce, ciphertext, tag, dataKey);
        return dataKey;
    }

    public Task DestroyKeyAsync(Guid keyId, Guid byUserId, CancellationToken ct) =>
        // Cryptographic erasure: mark destroyed AND scrub the wrapping salt embedded in key_reference so the
        // KEK can never be re-derived → every data key wrapped under it is permanently unrecoverable.
        db.Database.ExecuteSqlRawAsync(
            """
            UPDATE platform.encryption_keys
            SET status = 'destroyed', destroyed_at = NOW(),
                key_reference = split_part(key_reference, '#', 1) || '#DESTROYED'
            WHERE key_id = @p0 AND status <> 'destroyed'
            """,
            new NpgsqlParameter("@p0", keyId));

    private async Task<KeyRef?> FindActiveAsync(Guid? tenantId, string dataClass, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<KeyRow>(
                """
                SELECT key_id AS "KeyId", tenant_id AS "TenantId", data_class AS "DataClass",
                       kms_provider AS "KmsProvider", key_reference AS "KeyReference", key_version AS "KeyVersion",
                       false AS "IsDestroyed"
                FROM platform.encryption_keys
                WHERE data_class = @p1 AND status = 'active'
                  AND (tenant_id = @p0 OR (@p0 IS NULL AND tenant_id IS NULL))
                ORDER BY key_version DESC LIMIT 1
                """,
                new NpgsqlParameter("@p0", (object?)tenantId ?? DBNull.Value),
                new NpgsqlParameter("@p1", dataClass))
            .ToListAsync(ct);
        var r = rows.FirstOrDefault();
        return r is null ? null : new KeyRef(r.KeyId, r.TenantId, r.DataClass, r.KmsProvider, r.KeyReference, r.KeyVersion, false);
    }

    /// <summary>Derives the 256-bit KEK from the master passphrase + the per-key salt embedded in key_reference.</summary>
    private (byte[] Kek, byte[] Salt) DeriveKek(KeyRef key)
    {
        var hashIndex = key.KeyReference.IndexOf('#');
        var saltPart = hashIndex >= 0 ? key.KeyReference[(hashIndex + 1)..] : key.KeyId.ToString();
        if (saltPart == "DESTROYED")
            throw new InvalidOperationException($"Key {key.KeyId} destroyed; KEK cannot be derived.");
        var salt = SafeFromBase64(saltPart, key.KeyId);
        var kek = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(_masterPassphrase), salt, 100_000, HashAlgorithmName.SHA256, 32);
        return (kek, salt);
    }

    private static byte[] SafeFromBase64(string s, Guid fallback)
    {
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return SHA256.HashData(Encoding.UTF8.GetBytes(fallback.ToString())); }
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, result, offset, p.Length); offset += p.Length; }
        return result;
    }

    private sealed record KeyRow(Guid KeyId, Guid? TenantId, string DataClass, string KmsProvider, string KeyReference, int KeyVersion, bool IsDestroyed);
}
