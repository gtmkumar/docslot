namespace mediq.SharedDataModel.Docslot.Dashboard.Dtos;

/// <summary>
/// Aggregated analytics for the reception-desk Analytics screen, scoped to one tenant and one period
/// (month / quarter / year). Every value is a tenant-level AGGREGATE — there is no PHI here (no patient
/// names, no phones). Monetary values are INR (<see cref="DashboardContract.CurrencyCode"/>); percentages
/// are 0..100. Day-bucket aggregates are computed against CURRENT_DATE in Asia/Kolkata (per the dashboard
/// timezone contract).
/// </summary>
public sealed record AnalyticsDto(
    AnalyticsKpisDto Kpis,
    IReadOnlyList<WeeklyVolumeDto> WeeklyVolume,
    IReadOnlyList<TopDepartmentDto> TopDepartments,
    IReadOnlyList<FunnelStageDto> Funnel);

/// <summary>Headline KPIs over the requested period. Percentages are 0..100; revenue is INR.</summary>
public sealed record AnalyticsKpisDto(
    int TotalBookings,
    decimal WhatsappSharePct,
    decimal NoShowRatePct,
    decimal Revenue,
    string RevenueCurrency);

/// <summary>One weekday bucket of the CURRENT week: WhatsApp vs. other booking counts (by slot_date weekday).</summary>
public sealed record WeeklyVolumeDto(string Weekday, int Whatsapp, int Other);

/// <summary>A department and its booking count over the period (top-N, descending).</summary>
public sealed record TopDepartmentDto(string DepartmentName, int Bookings);

/// <summary>One stage of the WhatsApp conversational funnel. Counts are monotonic non-increasing; pct is 0..100 relative to stage 1.</summary>
public sealed record FunnelStageDto(string Stage, int Count, decimal Pct);
