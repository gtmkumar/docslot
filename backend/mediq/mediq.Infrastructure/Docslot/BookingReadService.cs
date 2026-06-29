using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.Dashboard;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;
using mediq.SharedDataModel.Docslot.Dashboard.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Docslot;

/// <summary>
/// Read-side projections for the reception-desk Dashboard (the exact contract the frontend already
/// consumes). Tenant-scoped; "today" computed in Asia/Kolkata; phone is ALWAYS masked (PHI). Projects via
/// raw SQL so day-bucket aggregates and slot-instant composition match the canonical view semantics.
/// </summary>
public sealed class BookingReadService(PlatformDbContext db) : IBookingReadService
{
    public async Task<DashboardSummaryDto> GetSummaryAsync(Guid tenantId, CancellationToken ct)
    {
        // Day buckets in IST: compare *_at AT TIME ZONE 'Asia/Kolkata' to today's IST date.
        var rows = await db.Database.SqlQueryRaw<SummaryRow>(
                """
                SELECT
                    COUNT(*) FILTER (WHERE b.status = 'pending')::int AS "LiveQueue",
                    COUNT(*) FILTER (WHERE b.status = 'confirmed'
                        AND (b.confirmed_at AT TIME ZONE 'Asia/Kolkata')::date = (NOW() AT TIME ZONE 'Asia/Kolkata')::date)::int AS "ConfirmedToday",
                    COALESCE(SUM(d.consultation_fee) FILTER (WHERE b.status IN ('confirmed','completed')
                        AND (b.booked_at AT TIME ZONE 'Asia/Kolkata')::date = (NOW() AT TIME ZONE 'Asia/Kolkata')::date), 0)::numeric AS "Revenue",
                    COUNT(*) FILTER (WHERE (b.no_show_at AT TIME ZONE 'Asia/Kolkata')::date = (NOW() AT TIME ZONE 'Asia/Kolkata')::date)::int AS "NoShowToday",
                    COUNT(*) FILTER (WHERE b.status IN ('completed','no_show','cancelled')
                        AND (b.booked_at AT TIME ZONE 'Asia/Kolkata')::date = (NOW() AT TIME ZONE 'Asia/Kolkata')::date)::int AS "TerminalToday"
                FROM docslot.bookings b
                LEFT JOIN docslot.doctors d ON d.doctor_id = b.doctor_id
                WHERE b.tenant_id = @p0
                """,
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);

        var r = rows.FirstOrDefault() ?? new SummaryRow(0, 0, 0m, 0, 0);
        var noShowRate = r.TerminalToday > 0 ? Math.Round((decimal)r.NoShowToday / r.TerminalToday, 4) : 0m;

        return new DashboardSummaryDto(
            r.LiveQueue, r.ConfirmedToday, r.Revenue, DashboardContract.CurrencyCode,
            noShowRate, DateTimeOffset.UtcNow);
    }

    public async Task<(IReadOnlyList<BookingListItemDto> Items, int Total)> ListAsync(BookingListFilter f, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<BookingRow>(
                """
                SELECT
                    b.booking_id AS "BookingId", b.booking_number AS "BookingNumber",
                    tok.token_number AS "TokenNumber",
                    COALESCE(b.patient_name_at_booking, p.full_name) AS "PatientName",
                    COALESCE(b.patient_phone_at_booking, p.phone_number) AS "RawPhone",
                    COALESCE(b.patient_age_at_booking, p.age) AS "Age",
                    p.gender AS "Gender",
                    COALESCE(d.display_name, d.full_name) AS "DoctorName",
                    dep.name AS "DepartmentName",
                    s.slot_date AS "SlotDate", s.start_time AS "StartTime", s.end_time AS "EndTime",
                    b.status AS "Status", b.booked_via AS "Source", b.notes AS "Note",
                    b.booked_at AS "BookedAt", p.preferred_language AS "Language",
                    b.booked_by_type AS "BookedByType", b.behalf_relation AS "BehalfRelation",
                    b.patient_consent_status AS "PatientConsentStatus", b.doctor_id AS "DoctorId"
                FROM docslot.bookings b
                JOIN docslot.time_slots s ON s.slot_id = b.slot_id
                JOIN docslot.doctors d ON d.doctor_id = b.doctor_id
                LEFT JOIN docslot.departments dep ON dep.department_id = b.department_id
                LEFT JOIN docslot.patients p ON p.patient_id = b.patient_id
                LEFT JOIN docslot.opd_tokens tok ON tok.booking_id = b.booking_id
                WHERE b.tenant_id = @p0
                  AND (@p1::varchar IS NULL OR b.status = @p1)
                  AND (@p2::date IS NULL OR s.slot_date = @p2)
                  AND (@p3::uuid IS NULL OR b.doctor_id = @p3)
                ORDER BY b.booked_at DESC
                OFFSET @p4 LIMIT @p5
                """,
                new NpgsqlParameter("@p0", f.TenantId),
                new NpgsqlParameter("@p1", (object?)f.Status ?? DBNull.Value),
                new NpgsqlParameter("@p2", (object?)(f.Date.HasValue ? f.Date.Value : (DateOnly?)null) ?? DBNull.Value),
                new NpgsqlParameter("@p3", (object?)f.DoctorId ?? DBNull.Value),
                new NpgsqlParameter("@p4", f.Skip),
                new NpgsqlParameter("@p5", Math.Clamp(f.Take, 1, 200)))
            .ToListAsync(ct);

        var totalRows = await db.Database.SqlQueryRaw<CountRow>(
                """
                SELECT COUNT(*)::int AS "Count" FROM docslot.bookings b
                JOIN docslot.time_slots s ON s.slot_id = b.slot_id
                WHERE b.tenant_id = @p0
                  AND (@p1::varchar IS NULL OR b.status = @p1)
                  AND (@p2::date IS NULL OR s.slot_date = @p2)
                  AND (@p3::uuid IS NULL OR b.doctor_id = @p3)
                """,
                new NpgsqlParameter("@p0", f.TenantId),
                new NpgsqlParameter("@p1", (object?)f.Status ?? DBNull.Value),
                new NpgsqlParameter("@p2", (object?)(f.Date.HasValue ? f.Date.Value : (DateOnly?)null) ?? DBNull.Value),
                new NpgsqlParameter("@p3", (object?)f.DoctorId ?? DBNull.Value))
            .ToListAsync(ct);

        return (rows.Select(Map).ToList(), totalRows.FirstOrDefault()?.Count ?? 0);
    }

    public async Task<BookingListItemDto?> GetItemAsync(Guid tenantId, Guid bookingId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<BookingRow>(
                """
                SELECT
                    b.booking_id AS "BookingId", b.booking_number AS "BookingNumber",
                    tok.token_number AS "TokenNumber",
                    COALESCE(b.patient_name_at_booking, p.full_name) AS "PatientName",
                    COALESCE(b.patient_phone_at_booking, p.phone_number) AS "RawPhone",
                    COALESCE(b.patient_age_at_booking, p.age) AS "Age",
                    p.gender AS "Gender",
                    COALESCE(d.display_name, d.full_name) AS "DoctorName",
                    dep.name AS "DepartmentName",
                    s.slot_date AS "SlotDate", s.start_time AS "StartTime", s.end_time AS "EndTime",
                    b.status AS "Status", b.booked_via AS "Source", b.notes AS "Note",
                    b.booked_at AS "BookedAt", p.preferred_language AS "Language",
                    b.booked_by_type AS "BookedByType", b.behalf_relation AS "BehalfRelation",
                    b.patient_consent_status AS "PatientConsentStatus", b.doctor_id AS "DoctorId"
                FROM docslot.bookings b
                JOIN docslot.time_slots s ON s.slot_id = b.slot_id
                JOIN docslot.doctors d ON d.doctor_id = b.doctor_id
                LEFT JOIN docslot.departments dep ON dep.department_id = b.department_id
                LEFT JOIN docslot.patients p ON p.patient_id = b.patient_id
                LEFT JOIN docslot.opd_tokens tok ON tok.booking_id = b.booking_id
                WHERE b.tenant_id = @p0 AND b.booking_id = @p1
                """,
                new NpgsqlParameter("@p0", tenantId), new NpgsqlParameter("@p1", bookingId))
            .ToListAsync(ct);
        var row = rows.FirstOrDefault();
        return row is null ? null : Map(row);
    }

    public async Task<NoShowFeatures?> GetNoShowFeaturesAsync(Guid tenantId, Guid bookingId, CancellationToken ct)
    {
        // Non-PHI features for no-show scoring: booking lead time (IST), the slot's hour, and whether it was a
        // booked-on-behalf booking (patient_consent_status <> 'not_required'). Tenant-scoped (+ RLS).
        var rows = await db.Database.SqlQueryRaw<FeatureRow>(
                """
                SELECT
                    GREATEST(0, (s.slot_date - (b.booked_at AT TIME ZONE 'Asia/Kolkata')::date))::int AS "LeadTimeDays",
                    EXTRACT(hour FROM s.start_time)::int AS "SlotHour",
                    (b.patient_consent_status <> 'not_required') AS "IsBehalfBooking"
                FROM docslot.bookings b
                JOIN docslot.time_slots s ON s.slot_id = b.slot_id
                WHERE b.tenant_id = @p0 AND b.booking_id = @p1
                """,
                new NpgsqlParameter("@p0", tenantId), new NpgsqlParameter("@p1", bookingId))
            .ToListAsync(ct);
        var r = rows.FirstOrDefault();
        return r is null ? null : new NoShowFeatures(r.LeadTimeDays, r.SlotHour, r.IsBehalfBooking);
    }

    public async Task<IReadOnlyList<ConversationMessageDto>> GetConversationAsync(Guid tenantId, Guid bookingId, CancellationToken ct)
    {
        // Conversation panel reads wa_message_log for the booking's patient within the tenant.
        var rows = await db.Database.SqlQueryRaw<ConvRow>(
                """
                SELECT w.log_id AS "LogId", w.direction AS "Direction", w.message_type AS "MessageType",
                       (w.content ->> 'text') AS "Content", w.status AS "Status", w.sent_at AS "SentAt"
                FROM docslot.wa_message_log w
                JOIN docslot.bookings b ON b.patient_id = w.patient_id AND b.tenant_id = w.tenant_id
                WHERE b.booking_id = @p1 AND w.tenant_id = @p0
                ORDER BY w.sent_at ASC
                LIMIT 200
                """,
                new NpgsqlParameter("@p0", tenantId), new NpgsqlParameter("@p1", bookingId))
            .ToListAsync(ct);

        return rows.Select(r => new ConversationMessageDto(
            r.LogId, r.Direction, r.MessageType, r.Content, r.Status,
            new DateTimeOffset(DateTime.SpecifyKind(r.SentAt, DateTimeKind.Utc)))).ToList();
    }

    private static BookingListItemDto Map(BookingRow r) => new(
        r.BookingId, r.BookingNumber, r.TokenNumber, r.PatientName,
        PhoneMasker.Mask(r.RawPhone), r.Age is null ? null : (int)r.Age,
        EnumParse.Gender(r.Gender),
        r.DoctorName, r.DepartmentName,
        ToIst(r.SlotDate, r.StartTime), ToIst(r.SlotDate, r.EndTime),
        EnumParse.Status(r.Status), EnumParse.Source(r.Source), r.Note,
        new DateTimeOffset(DateTime.SpecifyKind(r.BookedAt, DateTimeKind.Utc)),
        EnumParse.Language(r.Language),
        r.BookedByType, r.BehalfRelation, r.PatientConsentStatus, r.DoctorId);

    private static DateTimeOffset ToIst(DateOnly date, TimeOnly time) =>
        new(date.ToDateTime(time), DashboardContract.TimeZoneOffset);

    private sealed record SummaryRow(int LiveQueue, int ConfirmedToday, decimal Revenue, int NoShowToday, int TerminalToday);
    private sealed record FeatureRow(int LeadTimeDays, int SlotHour, bool IsBehalfBooking);
    private sealed record CountRow(int Count);
    private sealed record BookingRow(
        Guid BookingId, string BookingNumber, int? TokenNumber, string? PatientName, string? RawPhone,
        short? Age, string? Gender, string? DoctorName, string? DepartmentName, DateOnly SlotDate,
        TimeOnly StartTime, TimeOnly EndTime, string Status, string Source, string? Note, DateTime BookedAt, string? Language,
        string BookedByType, string? BehalfRelation, string PatientConsentStatus, Guid DoctorId);
    private sealed record ConvRow(Guid LogId, string Direction, string MessageType, string? Content, string? Status, DateTime SentAt);
}
