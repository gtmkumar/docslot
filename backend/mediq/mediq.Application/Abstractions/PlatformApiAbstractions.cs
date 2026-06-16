using mediq.Domain.PlatformApi;
using mediq.SharedDataModel.Docslot.PlatformApi;

namespace mediq.Application.Abstractions;

/// <summary>Reads/writes <c>platform_api.api_clients</c> + the client's grantable scope set.</summary>
public interface IApiClientRepository
{
    Task<ApiClient?> GetByCodeAsync(string clientCode, CancellationToken ct);
    Task<ApiClient?> GetByIdAsync(Guid clientId, CancellationToken ct);
    Task<IReadOnlyList<ApiClientDto>> ListAsync(int skip, int take, CancellationToken ct);

    /// <summary>The scope keys this client is permitted to request (active, unexpired grants).</summary>
    Task<IReadOnlySet<string>> GetGrantedScopeKeysAsync(Guid clientId, CancellationToken ct);

    Task<Guid> CreateAsync(RegisterApiClientRequest request, string secretHash, DateTime nowUtc, CancellationToken ct);
    Task UpdateSecretHashAsync(Guid clientId, string secretHash, CancellationToken ct);
    Task SetStatusAsync(Guid clientId, bool isActive, bool isVerified, Guid? verifiedBy, DateTime nowUtc, CancellationToken ct);
    Task SetRateLimitsAsync(Guid clientId, int perMinute, int perDay, int burst, CancellationToken ct);
    Task SetScopesAsync(Guid clientId, IReadOnlyList<string> scopeKeys, Guid? grantedBy, DateTime nowUtc, CancellationToken ct);
    Task TouchLastUsedAsync(Guid clientId, DateTime nowUtc, CancellationToken ct);
}

/// <summary>Reads the <c>platform_api.api_scopes</c> registry.</summary>
public interface IApiScopeRepository
{
    Task<IReadOnlyList<ScopeDto>> ListAsync(CancellationToken ct);
    Task<IReadOnlySet<string>> ExistingScopeKeysAsync(IReadOnlyCollection<string> candidates, CancellationToken ct);
}

/// <summary>Persists/revokes issued client tokens (<c>platform_api.api_tokens</c>) — hashes only.</summary>
public interface IApiTokenStore
{
    Task CreateAsync(Guid clientId, string tokenHash, IReadOnlyCollection<string> requested,
        IReadOnlyCollection<string> granted, Guid? tenantId, DateTime expiresUtc, CancellationToken ct);

    /// <summary>Looks up a live (unrevoked, unexpired) token by hash; returns its granted scopes + tenant.</summary>
    Task<ApiTokenLookup?> FindLiveByHashAsync(string tokenHash, CancellationToken ct);

    Task RevokeByHashAsync(string tokenHash, string reason, CancellationToken ct);
}

public sealed record ApiTokenLookup(Guid TokenId, Guid ClientId, Guid? TenantId, IReadOnlySet<string> GrantedScopes);

/// <summary>Appends to <c>platform_api.api_requests</c> for analytics + abuse detection (request log).</summary>
public interface IApiRequestLogWriter
{
    Task RecordAsync(ApiRequestLogEntry entry, CancellationToken ct);

    /// <summary>Count of requests for a client within the trailing window — backs per-client rate limiting.</summary>
    Task<int> CountRecentAsync(Guid clientId, TimeSpan window, CancellationToken ct);
}

public sealed record ApiRequestLogEntry(
    Guid? ClientId, Guid? TokenId, Guid? TenantId, string Method, string Path,
    string? IpAddress, string? UserAgent, int StatusCode, int? ResponseTimeMs, string? ErrorCode);

/// <summary>
/// Reads the <c>platform_api.api_requests</c> log for the developers/Logs surface. Returns ONLY
/// request metadata (method/path/status/latency/scope/client/time) — never bodies, IP, or PHI.
/// Filterable by client and date window; offset-paginated.
/// </summary>
public interface IApiRequestLogReader
{
    Task<ApiRequestLogPage> ListAsync(ApiRequestLogFilter filter, CancellationToken ct);
}

public sealed record ApiRequestLogFilter(Guid? ClientId, DateTimeOffset? From, DateTimeOffset? To, int Page, int PageSize);

public sealed record ApiRequestLogRow(
    Guid RequestId, Guid? ClientId, string? ClientName, string Method, string Path,
    string? ScopeUsed, int StatusCode, int? ResponseTimeMs, DateTime OccurredAt);

public sealed record ApiRequestLogPage(IReadOnlyList<ApiRequestLogRow> Items, int Total, int Page, int PageSize);

/// <summary>The scopes carried by the current request's client-credentials token (resolve once, in memory).</summary>
public interface IScopeContext
{
    bool IsClientToken { get; }
    IReadOnlySet<string> Scopes { get; }
    Guid? ClientId { get; }
    void Set(Guid clientId, IReadOnlySet<string> scopes);
    bool Has(string scopeKey);
}
