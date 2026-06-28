using mediq.Application.Abstractions;
using mediq.Domain.Commission;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace mediq.Infrastructure.Commission;

/// <summary>
/// The attribution ledger. <see cref="AddAsync"/> INSERTs the row; the DB trigger
/// <c>trg_no_attribution_on_discounted</c> raises a check_violation (SQLSTATE 23514) when the booking carries
/// a direct-booking discount — caught here and translated to
/// <see cref="AttributionOnDiscountedBookingException"/> so the discount↔attribution exclusivity surfaces as
/// a clean 422 rather than a leaked DB error.
/// </summary>
public sealed class AttributionRepository(PlatformDbContext db) : IAttributionRepository
{
    public async Task AddAsync(Attribution a, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO commission.attributions
                    (attribution_id, tenant_id, booking_id, broker_id, attribution_source, verification_status,
                     rule_id, commission_amount_inr, commission_status, fraud_score, fraud_flags, attributed_at, created_at, updated_at)
                VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, 'pending', @p8, @p9, @p10, @p10, @p10)
                """,
                P(("@p0", a.AttributionId), ("@p1", a.TenantId), ("@p2", a.BookingId), ("@p3", a.BrokerId),
                  ("@p4", a.AttributionSource), ("@p5", a.VerificationStatus),
                  ("@p6", (object?)a.RuleId ?? DBNull.Value), ("@p7", (object?)a.CommissionAmountInr ?? DBNull.Value),
                  ("@p8", (object?)a.FraudScore ?? DBNull.Value), ("@p9", a.FraudFlags), ("@p10", a.AttributedAt)));
        }
        catch (PostgresException ex) when (ex.SqlState == "23514")
        {
            // The discount-exclusivity trigger fired (or another CHECK). Surface the exclusivity rule cleanly.
            throw new AttributionOnDiscountedBookingException(a.BookingId);
        }
    }

    public Task<bool> ExistsAsync(Guid bookingId, Guid brokerId, CancellationToken ct) =>
        db.Database.SqlQueryRaw<bool>(
                "SELECT EXISTS(SELECT 1 FROM commission.attributions WHERE booking_id=@p0 AND broker_id=@p1) AS \"Value\"",
                P(("@p0", bookingId), ("@p1", brokerId)))
            .FirstAsync(ct);

    public async Task<BookingValue?> GetBookingValueAsync(Guid bookingId, Guid tenantId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<BookingValueRow>(
                """
                SELECT COALESCE(d.consultation_fee, 0) AS "AmountInr", b.direct_discount_inr AS "DirectDiscountInr",
                       b.booking_type AS "ServiceType", b.patient_id AS "PatientId"
                FROM docslot.bookings b LEFT JOIN docslot.doctors d ON d.doctor_id = b.doctor_id
                WHERE b.booking_id = @p0 AND b.tenant_id = @p1
                """,
                P(("@p0", bookingId), ("@p1", tenantId)))
            .ToListAsync(ct);
        var r = rows.FirstOrDefault();
        return r is null ? null : new BookingValue(r.AmountInr, r.DirectDiscountInr, r.ServiceType, r.PatientId);
    }

    public Task<int> CountRecentByBrokerAsync(Guid brokerId, TimeSpan window, CancellationToken ct) =>
        db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM commission.attributions WHERE broker_id=@p0 AND attributed_at >= @p1",
                P(("@p0", brokerId), ("@p1", DateTime.UtcNow - window)))
            .FirstAsync(ct);

    public Task<bool> BookingPatientReferredBySelfAsync(Guid bookingId, Guid brokerId, CancellationToken ct) =>
        // Self-referral: the broker's own phone == the booking patient's phone.
        db.Database.SqlQueryRaw<bool>(
                """
                SELECT EXISTS(
                    SELECT 1 FROM docslot.bookings b
                    JOIN docslot.patients p ON p.patient_id = b.patient_id
                    JOIN commission.brokers br ON br.broker_id = @p1
                    WHERE b.booking_id = @p0 AND p.phone_number = br.phone
                ) AS "Value"
                """,
                P(("@p0", bookingId), ("@p1", brokerId)))
            .FirstAsync(ct);

    public Task<decimal> BrokerEarnedThisMonthAsync(Guid brokerId, Guid tenantId, DateTime nowUtc, CancellationToken ct) =>
        db.Database.SqlQueryRaw<decimal>(
                """
                SELECT COALESCE(SUM(commission_amount_inr), 0)::numeric AS "Value"
                FROM commission.attributions
                WHERE broker_id = @p0 AND tenant_id = @p1
                  AND (attributed_at AT TIME ZONE 'Asia/Kolkata') >= date_trunc('month', NOW() AT TIME ZONE 'Asia/Kolkata')
                  AND commission_status <> 'rejected'
                """,
                P(("@p0", brokerId), ("@p1", tenantId)))
            .FirstAsync(ct);

    public async Task<IReadOnlyList<Guid>> ReadyToPayAttributionIdsAsync(Guid tenantId, Guid brokerId, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<Guid>(
                "SELECT attribution_id AS \"Value\" FROM commission.attributions WHERE tenant_id=@p0 AND broker_id=@p1 AND commission_status='ready_to_pay'",
                P(("@p0", tenantId), ("@p1", brokerId)))
            .ToListAsync(ct);

    public Task<decimal> ReadyToPayGrossAsync(Guid tenantId, Guid brokerId, CancellationToken ct) =>
        db.Database.SqlQueryRaw<decimal>(
                "SELECT COALESCE(SUM(commission_amount_inr),0)::numeric AS \"Value\" FROM commission.attributions WHERE tenant_id=@p0 AND broker_id=@p1 AND commission_status='ready_to_pay'",
                P(("@p0", tenantId), ("@p1", brokerId)))
            .FirstAsync(ct);

    public Task MarkPaidAsync(IReadOnlyList<Guid> attributionIds, Guid payoutId, DateTime nowUtc, CancellationToken ct)
    {
        if (attributionIds.Count == 0) return Task.CompletedTask;
        return db.Database.ExecuteSqlRawAsync(
            "UPDATE commission.attributions SET commission_status='paid', paid_at=@p1, payout_id=@p2 WHERE attribution_id = ANY(@p0)",
            P(("@p0", attributionIds.ToArray()), ("@p1", nowUtc), ("@p2", payoutId)));
    }

    public async Task<IReadOnlyList<EarnedAttribution>> MarkEarnedForBookingAsync(Guid tenantId, Guid bookingId, DateTime now, CancellationToken ct)
    {
        // pending → earned, but ONLY for attributions whose verification has cleared (auto/confirmed/override).
        // A post-hoc claim awaiting patient confirmation stays 'pending' until verified. RLS-scoped by tenant.
        var rows = await db.Database.SqlQueryRaw<EarnRow>(
                """
                UPDATE commission.attributions
                SET commission_status='earned', earned_at=@p2, updated_at=@p2
                WHERE tenant_id=@p0 AND booking_id=@p1 AND commission_status='pending'
                  AND verification_status IN ('auto_verified','patient_confirmed','admin_override')
                RETURNING broker_id AS "BrokerId", COALESCE(commission_amount_inr,0)::numeric AS "Amount"
                """,
                P(("@p0", tenantId), ("@p1", bookingId), ("@p2", now)))
            .ToListAsync(ct);
        return rows.Select(r => new EarnedAttribution(r.BrokerId, r.Amount)).ToList();
    }

    public async Task<IReadOnlyList<ReversedAttribution>> MarkReversedForBookingAsync(Guid tenantId, Guid bookingId, DateTime now, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<ReverseRow>(
                """
                WITH affected AS (
                    SELECT attribution_id, broker_id, COALESCE(commission_amount_inr,0) AS amt, commission_status AS prev
                    FROM commission.attributions
                    WHERE tenant_id=@p0 AND booking_id=@p1 AND commission_status IN ('pending','earned','ready_to_pay')
                    FOR UPDATE
                )
                UPDATE commission.attributions a SET commission_status='reversed', updated_at=@p2
                FROM affected WHERE a.attribution_id = affected.attribution_id
                RETURNING affected.broker_id AS "BrokerId", affected.amt::numeric AS "Amount", affected.prev AS "FromStatus"
                """,
                P(("@p0", tenantId), ("@p1", bookingId), ("@p2", now)))
            .ToListAsync(ct);
        return rows.Select(r => new ReversedAttribution(r.BrokerId, r.Amount, r.FromStatus)).ToList();
    }

    public async Task<ReversedAttribution?> ReverseOneAsync(Guid attributionId, Guid tenantId, DateTime now, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<ReverseRow>(
                """
                WITH affected AS (
                    SELECT attribution_id, broker_id, COALESCE(commission_amount_inr,0) AS amt, commission_status AS prev
                    FROM commission.attributions
                    WHERE attribution_id=@p0 AND tenant_id=@p1 AND commission_status IN ('pending','earned','ready_to_pay','paid')
                    FOR UPDATE
                )
                UPDATE commission.attributions a SET commission_status='reversed', updated_at=@p2
                FROM affected WHERE a.attribution_id = affected.attribution_id
                RETURNING affected.broker_id AS "BrokerId", affected.amt::numeric AS "Amount", affected.prev AS "FromStatus"
                """,
                P(("@p0", attributionId), ("@p1", tenantId), ("@p2", now)))
            .ToListAsync(ct);
        var r = rows.FirstOrDefault();
        return r is null ? null : new ReversedAttribution(r.BrokerId, r.Amount, r.FromStatus);
    }

    public Task WriteDirectDiscountAsync(Guid bookingId, Guid tenantId, decimal discountInr, Guid ruleId, DateTime now, CancellationToken ct) =>
        // Runs in the booking-creation UoW (app.tenant_id set) → bookings RLS allows own-tenant. The discount
        // makes the booking ineligible for any broker attribution (enforced by trg_no_attribution_on_discounted).
        db.Database.ExecuteSqlRawAsync(
            "UPDATE docslot.bookings SET direct_discount_inr=@p2, direct_discount_rule_id=@p3, updated_at=@p4 WHERE booking_id=@p0 AND tenant_id=@p1",
            P(("@p0", bookingId), ("@p1", tenantId), ("@p2", discountInr), ("@p3", ruleId), ("@p4", now)));

    public Task<int> SettleEarnedAsync(TimeSpan window, CancellationToken ct) =>
        // SECURITY DEFINER fn (cross-tenant; the settlement worker has no app.tenant_id).
        db.Database.SqlQueryRaw<int>(
                "SELECT commission.settle_earned_attributions(make_interval(secs => @p0)) AS \"Value\"",
                new NpgsqlParameter("@p0", (int)window.TotalSeconds))
            .FirstAsync(ct);

    private sealed record EarnRow(Guid BrokerId, decimal Amount);
    private sealed record ReverseRow(Guid BrokerId, decimal Amount, string FromStatus);

    public async Task<IReadOnlyList<SharedDataModel.Docslot.Commission.AttributionListItemDto>> ListByTenantAsync(
        Guid tenantId, int skip, int take, CancellationToken ct)
    {
        // fraud_flags is text[] → read via a direct reader (avoids EF array-mapping pitfalls). PHI: the
        // patient is reduced to a FIRST NAME + MASKED phone here; the raw phone never leaves this seam.
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT a.attribution_id, COALESCE(bk.booking_number, '') AS booking_ref, a.broker_id,
                   COALESCE(b.full_name, '') AS broker_name, COALESCE(p.full_name, '') AS patient_name, p.phone_number AS patient_phone,
                   a.attribution_source, a.verification_status, a.commission_status, a.commission_amount_inr,
                   a.fraud_score, COALESCE(a.fraud_flags, '{}') AS fraud_flags, a.created_at
            FROM commission.attributions a
            LEFT JOIN commission.brokers b ON b.broker_id = a.broker_id
            LEFT JOIN docslot.bookings bk ON bk.booking_id = a.booking_id
            LEFT JOIN docslot.patients p ON p.patient_id = bk.patient_id
            WHERE a.tenant_id = @p0
            ORDER BY a.created_at DESC OFFSET @p1 LIMIT @p2
            """, conn);
        // Enlist the current EF (tenant-scoped) transaction so the SET LOCAL app.tenant_id GUC is in scope —
        // otherwise this raw command runs outside the tx and RLS on commission.attributions returns 0 rows.
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        cmd.Parameters.AddWithValue("@p0", tenantId);
        cmd.Parameters.AddWithValue("@p1", skip);
        cmd.Parameters.AddWithValue("@p2", take);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<SharedDataModel.Docslot.Commission.AttributionListItemDto>();
        while (await rd.ReadAsync(ct))
        {
            var fullName = rd.IsDBNull(4) ? "" : rd.GetString(4);
            var firstName = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var flags = rd.IsDBNull(11) ? Array.Empty<string>() : (string[])rd.GetValue(11);
            result.Add(new SharedDataModel.Docslot.Commission.AttributionListItemDto(
                rd.GetGuid(0), rd.GetString(1), rd.GetGuid(2), rd.GetString(3),
                firstName, mediq.Infrastructure.Docslot.PhoneMasker.Mask(rd.IsDBNull(5) ? null : rd.GetString(5)),
                rd.GetString(6), rd.GetString(7), rd.GetString(8),
                rd.IsDBNull(9) ? null : rd.GetDecimal(9), rd.IsDBNull(10) ? 0m : rd.GetDecimal(10),
                flags, new DateTimeOffset(DateTime.SpecifyKind(rd.GetDateTime(12), DateTimeKind.Utc))));
        }
        return result;
    }

    private static object[] P(params (string Name, object Value)[] ps) =>
        ps.Select(p => (object)new NpgsqlParameter(p.Name, p.Value)).ToArray();

    private sealed record BookingValueRow(decimal AmountInr, decimal DirectDiscountInr, string? ServiceType, Guid PatientId);
}
