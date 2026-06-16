using mediq.Api.Authorization;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Features.Docslot.Queries;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace mediq.Api.Controllers;

/// <summary>
/// Tenant analytics for the Analytics screen. Gated by <c>docslot.analytics.read</c>. Every value is a
/// tenant-level AGGREGATE (no PHI). The period (month|quarter|year, default month) bounds the booked_at /
/// slot_date range in Asia/Kolkata.
/// </summary>
[ApiController]
[Route("api/v1/analytics")]
[Authorize]
public sealed class AnalyticsController(IQueryDispatcher queries, ICurrentUserContext currentUser) : ControllerBase
{
    [HttpGet]
    [RequirePermission("docslot.analytics.read")]
    [ProducesResponseType<AnalyticsDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AnalyticsDto>> Get([FromQuery] string? period, CancellationToken ct)
        => Ok(await queries.Query(new GetAnalyticsQuery(RequireTenant(), ParsePeriod(period)), ct));

    private static AnalyticsPeriod ParsePeriod(string? period) => period?.Trim().ToLowerInvariant() switch
    {
        "quarter" => AnalyticsPeriod.Quarter,
        "year" => AnalyticsPeriod.Year,
        _ => AnalyticsPeriod.Month,
    };

    private Guid RequireTenant() =>
        currentUser.TenantId ?? throw new mediq.Utilities.Exceptions.ForbiddenException("No active tenant for this session.");
}
