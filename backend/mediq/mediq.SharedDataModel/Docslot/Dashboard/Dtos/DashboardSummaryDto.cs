namespace mediq.SharedDataModel.Docslot.Dashboard.Dtos;

/// <summary>
/// Read-model for the reception-desk dashboard's four stat cards.
/// <para>
/// Sourced from <c>docslot.bookings</c> aggregates, aligned with the canonical view
/// <c>docslot.v_tenant_today_overview</c> (database/03_docslot.sql) and the
/// <c>idx_bookings_pending</c> partial index. All counts are scoped to the caller's
/// active tenant (<c>tenant_id</c>) and to "today" in <b>Asia/Kolkata</b>
/// (see <see cref="DashboardContract"/>): the API computes CURRENT_DATE / status
/// filters against IST, not UTC.
/// </para>
/// </summary>
/// <param name="LiveQueueCount">
/// Card 1 — "Live queue". Count of bookings with status = <c>pending</c> for the tenant.
/// Maps to <c>COUNT(*) FILTER (WHERE b.status = 'pending')</c>
/// (<c>v_tenant_today_overview.pending_approvals</c>). Same source as the
/// <c>pending_bookings_count</c> nav badge.
/// </param>
/// <param name="ConfirmedTodayCount">
/// Card 2 — "Confirmed today". Count of bookings whose slot/confirmation falls on today (IST)
/// with status = <c>confirmed</c>. Derived from <c>docslot.bookings</c>
/// (status = 'confirmed' AND confirmed_at::DATE = CURRENT_DATE at IST).
/// </param>
/// <param name="TodayRevenue">
/// Card 3 — "Today's revenue". Sum of consultation fees for today's realized bookings.
/// Derived by joining completed/confirmed <c>docslot.bookings</c> to
/// <c>docslot.doctors.consultation_fee</c> / <c>follow_up_fee</c> (DECIMAL(10,2)).
/// <c>decimal</c> preserves money precision; never use float for currency.
/// </param>
/// <param name="RevenueCurrency">
/// ISO-4217 currency for <paramref name="TodayRevenue"/>. The platform is India-only
/// (<c>docslot.patients.country DEFAULT 'IN'</c>), so this is <c>"INR"</c>; carried
/// explicitly so the frontend never hardcodes the symbol.
/// </param>
/// <param name="NoShowRate">
/// Card 4 — "No-show rate". Fraction in [0,1] = no-show bookings / total terminal bookings
/// for the window. Numerator maps to
/// <c>COUNT(*) FILTER (WHERE b.no_show_at::DATE = CURRENT_DATE)</c>
/// (<c>v_tenant_today_overview.no_shows_today</c>) over the comparable denominator.
/// A rate (not a raw count) so the card can render a percentage without a second call.
/// </param>
/// <param name="AsOf">
/// Server timestamp the snapshot was computed at, as a UTC-offset instant
/// (<see cref="DateTimeOffset"/>). Lets the UI show "as of HH:mm" and detect staleness.
/// </param>
public sealed record DashboardSummaryDto(
    int LiveQueueCount,
    int ConfirmedTodayCount,
    decimal TodayRevenue,
    string RevenueCurrency,
    decimal NoShowRate,
    DateTimeOffset AsOf);
