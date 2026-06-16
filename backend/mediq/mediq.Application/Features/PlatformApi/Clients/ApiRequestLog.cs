using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.PlatformApi;

namespace mediq.Application.Features.PlatformApi.Clients;

/// <summary>
/// Paginated API request-log read (developers → Logs tab). Gated by <c>platform.api_clients.manage</c>.
/// Metadata only — no bodies, IP, or PHI. Mirrors the FE <c>ApiRequestLogPageSchema</c>.
/// </summary>
public sealed record ListApiRequestsQuery(Guid? ClientId, DateTimeOffset? From, DateTimeOffset? To, int Page, int PageSize)
    : IQuery<ApiRequestLogPageDto>;

public sealed class ListApiRequestsQueryHandler(IApiRequestLogReader reader)
    : IQueryHandler<ListApiRequestsQuery, ApiRequestLogPageDto>
{
    public async Task<ApiRequestLogPageDto> Handle(ListApiRequestsQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 200);
        var result = await reader.ListAsync(new ApiRequestLogFilter(q.ClientId, q.From, q.To, page, pageSize), ct);
        var items = result.Items
            .Select(r => new ApiRequestLogDto(
                r.RequestId, r.ClientId, r.ClientName, r.Method, r.Path, r.ScopeUsed, r.StatusCode, r.ResponseTimeMs,
                new DateTimeOffset(DateTime.SpecifyKind(r.OccurredAt, DateTimeKind.Utc))))
            .ToList();
        return new ApiRequestLogPageDto(items, result.Total, result.Page, result.PageSize);
    }
}
