using System.Collections.Concurrent;
using mediq.Application.Abstractions;

namespace mediq.Infrastructure.Storage;

/// <summary>
/// In-memory blob adapter (tests + fully-ephemeral dev). Holds opaque (already-encrypted) bytes keyed by a
/// tenant-namespaced key. Registered as a singleton so the store survives across requests.
/// </summary>
public sealed class InMemoryBlobStorage : IBlobStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _store = new(StringComparer.Ordinal);

    public Task<StoredBlobRef> PutAsync(
        Guid tenantId, string resourceType, Guid resourceId, string fileName, byte[] content, CancellationToken ct)
    {
        var key = $"{tenantId:N}/{resourceType}/{resourceId:N}/{Guid.CreateVersion7():N}";
        _store[key] = content;
        return Task.FromResult(new StoredBlobRef(key, content.LongLength));
    }

    public Task<byte[]?> GetAsync(string storageKey, Guid tenantId, CancellationToken ct)
    {
        // Cross-tenant guard: a key must live in the caller's tenant namespace.
        if (!storageKey.StartsWith($"{tenantId:N}/", StringComparison.Ordinal))
            return Task.FromResult<byte[]?>(null);
        return Task.FromResult(_store.TryGetValue(storageKey, out var bytes) ? bytes : null);
    }
}
