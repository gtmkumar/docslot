using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Commission;

/// <summary>
/// Form 16A (TDS u/s 194H) certificate persistence over <c>commission.tds_certificates</c> + the joined source
/// data (paid payout + deductor tenant + deductee broker). The deductee PAN is returned ONLY as the encrypted
/// envelope (<see cref="Form16ASource.BrokerPanEnvelope"/>); the handler decrypts it transiently and persists
/// just the last 4. RLS-scoped to the active tenant.
/// </summary>
public sealed class TdsCertificateRepository(PlatformDbContext db) : ITdsCertificateRepository
{
    public async Task<Form16ASource?> GetSourceAsync(Guid payoutId, Guid tenantId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<SourceRow>(
                """
                SELECT p.payout_id AS "PayoutId", p.status AS "Status", p.invoice_number AS "InvoiceNumber",
                       p.gross_amount_inr AS "GrossInr", p.tds_rate AS "TdsRate", p.tds_amount_inr AS "TdsInr",
                       p.completed_at AS "CompletedAt", p.broker_id AS "BrokerId", b.full_name AS "BrokerName",
                       b.pan_number AS "BrokerPanEnvelope", t.legal_name AS "DeductorName", t.tan AS "DeductorTan", t.pan AS "DeductorPan"
                FROM commission.payouts p
                JOIN commission.brokers b ON b.broker_id = p.broker_id
                JOIN platform.tenants t ON t.tenant_id = p.tenant_id
                WHERE p.payout_id = @p0 AND p.tenant_id = @p1
                """,
                P(("@p0", payoutId), ("@p1", tenantId)))
            .ToListAsync(ct);
        var r = rows.FirstOrDefault();
        return r is null ? null : new Form16ASource(
            r.PayoutId, r.Status, r.InvoiceNumber, r.GrossInr, r.TdsRate, r.TdsInr, r.CompletedAt, r.BrokerId,
            r.BrokerName, r.BrokerPanEnvelope, r.DeductorName, r.DeductorTan, r.DeductorPan);
    }

    public async Task<Guid> UpsertAsync(TdsCertificateRecord c, CancellationToken ct)
    {
        // One certificate per payout — a re-issue updates the existing row (status reset to provisional, regenerated).
        var ids = await db.Database.SqlQueryRaw<Guid>(
                """
                INSERT INTO commission.tds_certificates
                    (certificate_id, tenant_id, payout_id, broker_id, section, financial_year, quarter,
                     deductor_name, deductor_tan, deductor_pan, deductee_name, deductee_pan_last4,
                     gross_amount_inr, tds_rate, tds_amount_inr, status, traces_certificate_number,
                     generated_by_user_id, generated_at, created_at, updated_at)
                VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, @p15, @p16, @p17, @p18, @p18, @p18)
                ON CONFLICT (payout_id) DO UPDATE SET
                     section = EXCLUDED.section, financial_year = EXCLUDED.financial_year, quarter = EXCLUDED.quarter,
                     deductor_name = EXCLUDED.deductor_name, deductor_tan = EXCLUDED.deductor_tan, deductor_pan = EXCLUDED.deductor_pan,
                     deductee_name = EXCLUDED.deductee_name, deductee_pan_last4 = EXCLUDED.deductee_pan_last4,
                     gross_amount_inr = EXCLUDED.gross_amount_inr, tds_rate = EXCLUDED.tds_rate, tds_amount_inr = EXCLUDED.tds_amount_inr,
                     status = EXCLUDED.status, generated_by_user_id = EXCLUDED.generated_by_user_id, generated_at = EXCLUDED.generated_at,
                     updated_at = EXCLUDED.updated_at
                RETURNING certificate_id AS "Value"
                """,
                P(("@p0", c.CertificateId), ("@p1", c.TenantId), ("@p2", c.PayoutId), ("@p3", c.BrokerId), ("@p4", c.Section),
                  ("@p5", c.FinancialYear), ("@p6", c.Quarter), ("@p7", c.DeductorName), ("@p8", (object?)c.DeductorTan ?? DBNull.Value),
                  ("@p9", (object?)c.DeductorPan ?? DBNull.Value), ("@p10", c.DeducteeName), ("@p11", (object?)c.DeducteePanLast4 ?? DBNull.Value),
                  ("@p12", c.GrossInr), ("@p13", c.TdsRate), ("@p14", c.TdsInr), ("@p15", c.Status),
                  ("@p16", (object?)c.TracesCertificateNumber ?? DBNull.Value), ("@p17", (object?)c.GeneratedByUserId ?? DBNull.Value),
                  ("@p18", c.GeneratedAt)))
            .ToListAsync(ct);
        return ids[0];
    }

    public async Task<TdsCertificateRecord?> GetByPayoutAsync(Guid payoutId, Guid tenantId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<CertRow>(
                """
                SELECT certificate_id AS "CertificateId", tenant_id AS "TenantId", payout_id AS "PayoutId", broker_id AS "BrokerId",
                       section AS "Section", financial_year AS "FinancialYear", quarter AS "Quarter",
                       deductor_name AS "DeductorName", deductor_tan AS "DeductorTan", deductor_pan AS "DeductorPan",
                       deductee_name AS "DeducteeName", deductee_pan_last4 AS "DeducteePanLast4",
                       gross_amount_inr AS "GrossInr", tds_rate AS "TdsRate", tds_amount_inr AS "TdsInr",
                       status AS "Status", traces_certificate_number AS "TracesCertificateNumber",
                       generated_by_user_id AS "GeneratedByUserId", generated_at AS "GeneratedAt"
                FROM commission.tds_certificates WHERE payout_id = @p0 AND tenant_id = @p1
                """,
                P(("@p0", payoutId), ("@p1", tenantId)))
            .ToListAsync(ct);
        var r = rows.FirstOrDefault();
        return r is null ? null : new TdsCertificateRecord(
            r.CertificateId, r.TenantId, r.PayoutId, r.BrokerId, r.Section, r.FinancialYear, r.Quarter,
            r.DeductorName, r.DeductorTan, r.DeductorPan, r.DeducteeName, r.DeducteePanLast4,
            r.GrossInr, r.TdsRate, r.TdsInr, r.Status, r.TracesCertificateNumber, r.GeneratedByUserId, r.GeneratedAt);
    }

    public Task SetPayoutForm16AUrlAsync(Guid payoutId, Guid tenantId, string url, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE commission.payouts SET form_16a_url = @p2, updated_at = @p3 WHERE payout_id = @p0 AND tenant_id = @p1",
            P(("@p0", payoutId), ("@p1", tenantId), ("@p2", url), ("@p3", nowUtc)));

    private static object[] P(params (string Name, object Value)[] ps) => ps.Select(p => (object)new NpgsqlParameter(p.Name, p.Value)).ToArray();

    private sealed record SourceRow(Guid PayoutId, string Status, string? InvoiceNumber, decimal GrossInr, decimal TdsRate,
        decimal TdsInr, DateTime? CompletedAt, Guid BrokerId, string BrokerName, string? BrokerPanEnvelope,
        string DeductorName, string? DeductorTan, string? DeductorPan);

    private sealed record CertRow(Guid CertificateId, Guid TenantId, Guid PayoutId, Guid BrokerId, string Section,
        string FinancialYear, string Quarter, string DeductorName, string? DeductorTan, string? DeductorPan,
        string DeducteeName, string? DeducteePanLast4, decimal GrossInr, decimal TdsRate, decimal TdsInr, string Status,
        string? TracesCertificateNumber, Guid? GeneratedByUserId, DateTime GeneratedAt);
}
