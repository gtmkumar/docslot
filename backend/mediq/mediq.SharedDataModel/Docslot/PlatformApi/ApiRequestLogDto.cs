namespace mediq.SharedDataModel.Docslot.PlatformApi;

/// <summary>
/// One API request-log row (maps to <c>platform_api.api_requests</c>). Built to match the FE
/// <c>ApiRequestLogSchema</c> (developers → Logs tab). Carries ONLY request metadata —
/// method/path/status/latency/scope/client + time. NEVER the request/response bodies, headers,
/// IP, or any PHI/secret.
/// </summary>
public sealed record ApiRequestLogDto(
    Guid RequestId,
    Guid? ClientId,
    string? ClientName,
    string Method,
    string Path,
    /// <summary>The scope used to authorise the call, if any (from the token). Null for unauthenticated/denied.</summary>
    string? ScopeUsed,
    int StatusCode,
    int? ResponseTimeMs,
    DateTimeOffset OccurredAt);

/// <summary>Offset page wrapper for the request-log list. Mirrors the FE <c>ApiRequestLogPageSchema</c>.</summary>
public sealed record ApiRequestLogPageDto(
    IReadOnlyList<ApiRequestLogDto> Items,
    int Total,
    int Page,
    int PageSize);
