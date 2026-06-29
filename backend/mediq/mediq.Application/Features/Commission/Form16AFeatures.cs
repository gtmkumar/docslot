using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Commission;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Commission;

/// <summary>
/// Form 16A (TDS u/s 194H) — the TDS certificate a tenant must issue a broker for the tax deducted on a paid
/// commission payout. Two operations, both gated by <c>commission.tds.issue</c>:
/// <list type="bullet">
///   <item><b>Issue</b> (<see cref="IssueForm16ACommand"/>) — for a PAID payout, assembles the certificate
///   (deductor TAN/PAN from the tenant, deductee PAN decrypted transiently from the broker, gross/TDS from the
///   payout, financial-year + quarter from the payment date), persists the METADATA (last-4 PAN only), and points
///   the payout's <c>form_16a_url</c> at the document. <b>Status is always 'provisional'</b> on issue — a real,
///   verifiable certificate number only exists after the quarterly TDS return is filed on the govt TRACES portal
///   (external), so we never fabricate <c>traces_certificate_number</c>.</item>
///   <item><b>Render document</b> (<see cref="GetForm16ADocumentCommand"/>) — re-decrypts the FULL PAN transiently
///   and renders the legal certificate on demand (the full PAN is never persisted at rest). It is a COMMAND, not a
///   query, so the PAN-access entry the encryption layer writes to <c>platform.key_usage_log</c> COMMITS.</item>
/// </list>
/// </summary>
public sealed record IssueForm16ACommand(Guid TenantId, Guid PayoutId) : ICommand<Form16ACertificateDto>;

public sealed class IssueForm16ACommandHandler(
    ITdsCertificateRepository certs, IFieldEncryptionService encryption,
    IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<IssueForm16ACommand, Form16ACertificateDto>
{
    public async Task<Form16ACertificateDto> Handle(IssueForm16ACommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var src = await certs.GetSourceAsync(command.PayoutId, command.TenantId, ct)
            ?? throw new KeyNotFoundException("Payout not found.");

        // TDS is deducted when the payment is made — a certificate only exists for a DISBURSED payout.
        if (src.Status != "paid")
            throw new BusinessRuleException($"Form 16A can only be issued for a paid payout (current: {src.Status}).");

        // Decrypt the deductee PAN transiently (purpose-of-use: tax certificate). Only the last 4 is persisted.
        string? panLast4 = null;
        if (!string.IsNullOrEmpty(src.BrokerPanEnvelope))
        {
            var pan = await encryption.DecryptAsync(BrokerFields.Pan, src.BrokerPanEnvelope,
                new EncryptionContext(userId, command.TenantId, "tds_certificate", command.PayoutId, ctx.IpAddress), ct);
            panLast4 = pan.Length >= 4 ? pan[^4..] : pan;
        }

        var (fy, quarter) = Form16A.IndianFyQuarter(src.CompletedAt ?? clock.UtcNow);

        var record = new TdsCertificateRecord(
            Guid.CreateVersion7(), command.TenantId, command.PayoutId, src.BrokerId, "194H", fy, quarter,
            src.DeductorName, src.DeductorTan, src.DeductorPan, src.BrokerName, panLast4,
            src.GrossInr, src.TdsRate, src.TdsInr, "provisional", TracesCertificateNumber: null, userId, clock.UtcNow);
        var certId = await certs.UpsertAsync(record, ct);

        var documentUrl = Form16A.DocumentUrl(command.PayoutId);
        await certs.SetPayoutForm16AUrlAsync(command.PayoutId, command.TenantId, documentUrl, clock.UtcNow, ct);

        // Audit — purpose-of-use recorded; NO full PAN in the summary (the key_usage_log captures the PAN access).
        await audit.RecordAsync(new AuditEntry(
            "issue_form16a", "tds_certificate", certId, src.InvoiceNumber, userId, command.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Issued provisional Form 16A (194H) for payout {src.InvoiceNumber}: FY {fy} {quarter}, gross ₹{src.GrossInr}, TDS ₹{src.TdsInr}"), ct);

        return new Form16ACertificateDto(
            certId, command.PayoutId, src.InvoiceNumber, "194H", fy, quarter, src.DeductorName, src.DeductorTan,
            src.BrokerName, panLast4, src.GrossInr, src.TdsRate, src.TdsInr, "provisional", null, documentUrl);
    }
}

/// <summary>
/// Renders the legal Form 16A document for a payout's certificate. A COMMAND (not a query) so the encryption
/// layer's <c>key_usage_log</c> PAN-access entry commits. Returns null if no certificate has been issued yet.
/// </summary>
public sealed record GetForm16ADocumentCommand(Guid TenantId, Guid PayoutId)
    : ICommand<Form16ADocumentResult?>, IDoNotCacheResponse;   // full-PAN document → never persist the response to the idempotency store

public sealed class GetForm16ADocumentCommandHandler(
    ITdsCertificateRepository certs, IFieldEncryptionService encryption, IForm16ADocumentRenderer renderer,
    ICurrentUserContext ctx)
    : ICommandHandler<GetForm16ADocumentCommand, Form16ADocumentResult?>
{
    public async Task<Form16ADocumentResult?> Handle(GetForm16ADocumentCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var cert = await certs.GetByPayoutAsync(command.PayoutId, command.TenantId, ct);
        if (cert is null)
            return null;   // not issued yet → 404 at the controller

        var src = await certs.GetSourceAsync(command.PayoutId, command.TenantId, ct);

        // Re-decrypt the FULL PAN transiently for the legal document (never persisted). The decrypt logs the
        // purpose-of-use access to key_usage_log (committed because this is a command).
        string? panFull = null;
        if (src is not null && !string.IsNullOrEmpty(src.BrokerPanEnvelope))
            panFull = await encryption.DecryptAsync(BrokerFields.Pan, src.BrokerPanEnvelope,
                new EncryptionContext(userId, command.TenantId, "tds_certificate", command.PayoutId, ctx.IpAddress), ct);

        var doc = new Form16ADocument(
            src?.InvoiceNumber, cert.Section, cert.FinancialYear, cert.Quarter, cert.Status,
            cert.DeductorName, cert.DeductorTan, cert.DeductorPan, cert.DeducteeName, panFull,
            cert.GrossInr, cert.TdsRate, cert.TdsInr, cert.TracesCertificateNumber, cert.GeneratedAt);

        return new Form16ADocumentResult(renderer.ContentType, renderer.Render(doc));
    }
}

/// <summary>Pure Form 16A helpers (Indian financial-year/quarter math + the document URL convention).</summary>
public static class Form16A
{
    /// <summary>Indian FY (Apr–Mar) + quarter for a payment date. e.g. 2026-06-29 → ("2026-27","Q1"), 2027-02-01 → ("2026-27","Q4").</summary>
    public static (string FinancialYear, string Quarter) IndianFyQuarter(DateTime d)
    {
        var fyStart = d.Month >= 4 ? d.Year : d.Year - 1;
        var fy = $"{fyStart}-{(fyStart + 1) % 100:D2}";
        var quarter = d.Month switch
        {
            >= 4 and <= 6 => "Q1",
            >= 7 and <= 9 => "Q2",
            >= 10 and <= 12 => "Q3",
            _ => "Q4",
        };
        return (fy, quarter);
    }

    public static string DocumentUrl(Guid payoutId) => $"/api/v1/commission/payouts/{payoutId}/form-16a/document";
}
