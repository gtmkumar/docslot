using System.Net;
using mediq.Application.Abstractions;

namespace mediq.Infrastructure.Commission;

/// <summary>
/// Dev/default Form 16A renderer: produces a complete, print-ready HTML certificate (browser "Print → Save as PDF"
/// yields the document). A production adapter would render a binary PDF and, once the quarterly TDS return is filed
/// on TRACES, stamp the real certificate number — both EXTERNAL steps, so this renderer clearly marks an
/// unfiled certificate as PROVISIONAL and never invents a TRACES number. The full PAN is rendered transiently and
/// never persisted (the caller decrypts it on demand).
/// </summary>
public sealed class HtmlForm16ADocumentRenderer : IForm16ADocumentRenderer
{
    public string ContentType => "text/html; charset=utf-8";

    public string Render(Form16ADocument m)
    {
        var provisional = !string.Equals(m.Status, "issued", StringComparison.OrdinalIgnoreCase)
                          && !string.Equals(m.Status, "filed", StringComparison.OrdinalIgnoreCase);
        var banner = provisional
            ? "<div class=\"banner\">PROVISIONAL — not yet filed on TRACES. This is not a valid TDS certificate until the quarterly return is filed and a certificate number is issued.</div>"
            : $"<div class=\"banner ok\">TRACES Certificate No: {E(m.TracesCertificateNumber)}</div>";

        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><title>Form 16A — {{E(m.DeducteeName)}}</title>
            <style>
              body{font-family:system-ui,Arial,sans-serif;color:#1a1a1a;max-width:780px;margin:24px auto;padding:0 16px}
              h1{font-size:18px;text-align:center;margin:4px 0}
              .sub{text-align:center;color:#555;font-size:12px;margin-bottom:16px}
              .banner{background:#fff4e5;border:1px solid #e0a458;border-radius:6px;padding:8px 12px;font-size:12px;margin:12px 0}
              .banner.ok{background:#e9f7ee;border-color:#5aa86f}
              table{width:100%;border-collapse:collapse;margin:12px 0;font-size:13px}
              td,th{border:1px solid #ccc;padding:6px 8px;text-align:left;vertical-align:top}
              th{background:#f4f4f4;width:38%}
              .amt{text-align:right;font-variant-numeric:tabular-nums}
              .foot{font-size:11px;color:#666;margin-top:16px}
            </style></head><body>
            <h1>FORM NO. 16A</h1>
            <div class="sub">Certificate under Section 203 of the Income-tax Act, 1961 for Tax Deducted at Source — Section 194H (Commission/Brokerage)</div>
            {{banner}}
            <table>
              <tr><th>Financial Year / Quarter</th><td>{{E(m.FinancialYear)}} &nbsp;·&nbsp; {{E(m.Quarter)}}</td></tr>
              <tr><th>Payout invoice</th><td>{{E(m.InvoiceNumber)}}</td></tr>
            </table>
            <table>
              <tr><th>Deductor (name)</th><td>{{E(m.DeductorName)}}</td></tr>
              <tr><th>Deductor TAN</th><td>{{E(m.DeductorTan) ?? "<em>not configured</em>"}}</td></tr>
              <tr><th>Deductor PAN</th><td>{{E(m.DeductorPan) ?? "—"}}</td></tr>
            </table>
            <table>
              <tr><th>Deductee (name)</th><td>{{E(m.DeducteeName)}}</td></tr>
              <tr><th>Deductee PAN</th><td>{{E(m.DeducteePanFull) ?? "<em>not on record</em>"}}</td></tr>
            </table>
            <table>
              <tr><th>Section</th><td>{{E(m.Section)}}</td></tr>
              <tr><th>Amount paid / credited</th><td class="amt">₹ {{m.GrossInr:N2}}</td></tr>
              <tr><th>Rate of TDS</th><td class="amt">{{m.TdsRate:N2}} %</td></tr>
              <tr><th>Tax deducted (TDS)</th><td class="amt">₹ {{m.TdsInr:N2}}</td></tr>
            </table>
            <div class="foot">Generated {{m.GeneratedAt:yyyy-MM-dd HH:mm}} UTC. Computer-generated document.</div>
            </body></html>
            """;
    }

    private static string? E(string? s) => s is null ? null : WebUtility.HtmlEncode(s);
}
