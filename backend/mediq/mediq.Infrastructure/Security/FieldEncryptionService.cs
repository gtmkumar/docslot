using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Registry-driven field-level encryption (Layer 3). For a field listed in
/// <c>platform.encrypted_fields_registry</c>, resolves its data_class → active KMS key, generates a
/// per-record AES-256-GCM data key (envelope encryption), and produces a self-describing base64 envelope
/// <c>{key_id, iv, ct, tag}</c>. Every encrypt/decrypt is logged to <c>platform.key_usage_log</c> for
/// forensics. Decrypt resolves the key by the envelope's key_id (works across rotation) and FAILS CLOSED
/// if the key was cryptographically destroyed (DPDP §12 erasure).
/// </summary>
public sealed class FieldEncryptionService(PlatformDbContext db, IKeyManagementService kms) : IFieldEncryptionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> IsRegisteredAsync(FieldRef field, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<BoolRow>(
                """
                SELECT (encryption_required) AS "Value" FROM platform.encrypted_fields_registry
                WHERE schema_name = @p0 AND table_name = @p1 AND column_name = @p2
                """,
                new NpgsqlParameter("@p0", field.Schema), new NpgsqlParameter("@p1", field.Table),
                new NpgsqlParameter("@p2", field.Column))
            .ToListAsync(ct) is [{ Value: true }, ..];

    public Task<string> EncryptAsync(FieldRef field, Guid? tenantId, string plaintext, EncryptionContext ctx, CancellationToken ct)
        => EncryptCoreAsync(field, tenantId, Encoding.UTF8.GetBytes(plaintext), ctx, ct);

    public Task<string> EncryptBytesAsync(FieldRef field, Guid? tenantId, byte[] plaintext, EncryptionContext ctx, CancellationToken ct)
        => EncryptCoreAsync(field, tenantId, plaintext, ctx, ct);

    public async Task<string> DecryptAsync(FieldRef field, string envelope, EncryptionContext ctx, CancellationToken ct)
        => Encoding.UTF8.GetString(await DecryptCoreAsync(envelope, ctx, ct));

    public Task<byte[]> DecryptBytesAsync(FieldRef field, string envelope, EncryptionContext ctx, CancellationToken ct)
        => DecryptCoreAsync(envelope, ctx, ct);

    private async Task<string> EncryptCoreAsync(FieldRef field, Guid? tenantId, byte[] plainBytes, EncryptionContext ctx, CancellationToken ct)
    {
        var dataClass = await ResolveDataClassAsync(field, ct);
        var key = await kms.GetActiveKeyAsync(tenantId, dataClass, ct);

        // Envelope encryption: random per-record data key, wrapped under the KMS key.
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var wrapped = kms.WrapDataKey(key, dataKey);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(dataKey, 16))
            aes.Encrypt(nonce, plainBytes, ciphertext, tag);
        CryptographicOperations.ZeroMemory(dataKey);

        var envelope = new Envelope(
            key.KeyId, Convert.ToBase64String(wrapped), Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions)));

        await LogUsageAsync(key.KeyId, "encrypt", ctx, success: true, error: null, ct);
        return payload;
    }

    private async Task<byte[]> DecryptCoreAsync(string envelope, EncryptionContext ctx, CancellationToken ct)
    {
        Envelope env;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(envelope));
            env = JsonSerializer.Deserialize<Envelope>(json, JsonOptions)!;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Malformed encryption envelope.", ex);
        }

        var key = await kms.GetKeyByIdAsync(env.KeyId, ct)
            ?? throw new InvalidOperationException($"Encryption key {env.KeyId} not found.");

        try
        {
            // Unwrap throws if the key was destroyed (cryptographic erasure) → fail closed.
            var dataKey = kms.UnwrapDataKey(key, Convert.FromBase64String(env.WrappedKey));
            var plain = new byte[Convert.FromBase64String(env.Ciphertext).Length];
            using (var aes = new AesGcm(dataKey, 16))
                aes.Decrypt(Convert.FromBase64String(env.Nonce), Convert.FromBase64String(env.Ciphertext),
                    Convert.FromBase64String(env.Tag), plain);
            CryptographicOperations.ZeroMemory(dataKey);

            await LogUsageAsync(key.KeyId, "decrypt", ctx, success: true, error: null, ct);
            return plain;
        }
        catch (Exception ex)
        {
            await LogUsageAsync(env.KeyId, "decrypt", ctx, success: false, error: ex.Message, ct);
            throw;
        }
    }

    private async Task<string> ResolveDataClassAsync(FieldRef field, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<DataClassRow>(
                """
                SELECT data_class AS "DataClass" FROM platform.encrypted_fields_registry
                WHERE schema_name = @p0 AND table_name = @p1 AND column_name = @p2
                """,
                new NpgsqlParameter("@p0", field.Schema), new NpgsqlParameter("@p1", field.Table),
                new NpgsqlParameter("@p2", field.Column))
            .ToListAsync(ct);
        return rows.FirstOrDefault()?.DataClass
            ?? throw new InvalidOperationException($"Field {field.Schema}.{field.Table}.{field.Column} is not in the encrypted_fields_registry.");
    }

    private Task LogUsageAsync(Guid keyId, string operation, EncryptionContext ctx, bool success, string? error, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform.key_usage_log
                (usage_id, key_id, operation, user_id, tenant_id, resource_type, resource_id, ip_address, success, error_message, occurred_at)
            VALUES (gen_random_uuid(), @p0, @p1, @p2, @p3, @p4, @p5, CAST(@p6 AS inet), @p7, @p8, NOW())
            """,
            new NpgsqlParameter("@p0", keyId),
            new NpgsqlParameter("@p1", operation),
            new NpgsqlParameter("@p2", (object?)ctx.UserId ?? DBNull.Value),
            new NpgsqlParameter("@p3", (object?)ctx.TenantId ?? DBNull.Value),
            new NpgsqlParameter("@p4", (object?)ctx.ResourceType ?? DBNull.Value),
            new NpgsqlParameter("@p5", (object?)ctx.ResourceId ?? DBNull.Value),
            new NpgsqlParameter("@p6", (object?)ctx.IpAddress ?? DBNull.Value),
            new NpgsqlParameter("@p7", success),
            new NpgsqlParameter("@p8", (object?)error ?? DBNull.Value));

    private sealed record Envelope(Guid KeyId, string WrappedKey, string Nonce, string Ciphertext, string Tag);
    private sealed record BoolRow(bool Value);
    private sealed record DataClassRow(string DataClass);
}
