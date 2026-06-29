using mediq.Application.Abstractions;
using mediq.Application.Options;
using Microsoft.Extensions.Options;

namespace mediq.Infrastructure.Storage;

/// <summary>
/// Dev blob adapter: writes opaque (already-encrypted) bytes under a tenant-namespaced key on the local
/// filesystem. Prod swaps in an object-store adapter (S3/GCS/Azure + provider SSE/KMS) behind IBlobStorage.
/// Stateless apart from the configured root → registered as a singleton.
/// </summary>
public sealed class LocalFileSystemBlobStorage : IBlobStorage
{
    private readonly string _root;

    public LocalFileSystemBlobStorage(IOptions<BlobStorageOptions> options)
        => _root = Path.GetFullPath(options.Value.RootPath);

    public async Task<StoredBlobRef> PutAsync(
        Guid tenantId, string resourceType, Guid resourceId, string fileName, byte[] content, CancellationToken ct)
    {
        var key = $"{tenantId:N}/{Sanitize(resourceType)}/{resourceId:N}/{Guid.CreateVersion7():N}{Extension(fileName)}";
        var path = ResolveWithinRoot(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, ct);
        return new StoredBlobRef(key, content.LongLength);
    }

    public async Task<byte[]?> GetAsync(string storageKey, Guid tenantId, CancellationToken ct)
    {
        // Cross-tenant guard: a key must live in the caller's tenant namespace.
        if (!storageKey.StartsWith($"{tenantId:N}/", StringComparison.Ordinal)) return null;
        var path = ResolveWithinRoot(storageKey);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    private string ResolveWithinRoot(string key)
    {
        // Defense against path traversal: the resolved path must stay under the storage root.
        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Resolved blob path escapes the storage root.");
        return full;
    }

    private static string Sanitize(string s)
        => new(s.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());

    private static string Extension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "");
        return ext.Length is > 1 and <= 10 && ext[1..].All(char.IsLetterOrDigit) ? ext.ToLowerInvariant() : "";
    }
}
