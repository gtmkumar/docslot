using System.Net.Http.Json;
using mediq.Application.Abstractions;
using mediq.Utilities.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace mediq.Infrastructure.Ai;

/// <summary>
/// Calls the AI sibling service's <c>POST /ai/v1/extractions/lab-report</c> OCR workflow. The caller's bearer
/// JWT + the declared purpose-of-use are forwarded (the AI service validates the same DocSlot JWT, runs its
/// own consent gate, and writes the purpose-of-use log). On a 5xx / network / timeout / deserialize failure
/// the adapter returns null (the caller surfaces "extraction unavailable"); an AI 4xx (authorization /
/// validation / not-found) is PROPAGATED as a typed exception so the gate is NEVER masked as "unavailable".
/// No PHI is logged (status only): not the sourceUrl, raw OCR text, or analyte values.
/// </summary>
public sealed class HttpAiOcrClient(
    HttpClient http, IHttpContextAccessor context, ILogger<HttpAiOcrClient> logger) : IAiOcrClient
{
    public async Task<OcrExtractionResult?> ExtractLabReportAsync(OcrExtractInput input, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/ai/v1/extractions/lab-report")
            {
                // No sourceUrl: .NET never forwards a client-controlled source path (the AI treats it as a local
                // filesystem path → arbitrary-file-read primitive). The AI uses its generated sample in dev; real
                // report-targeting is a pre-prod feature resolving a SERVER-controlled blob handle on the AI side.
                Content = JsonContent.Create(new
                {
                    relatedPatientId = input.RelatedPatientId.ToString(),
                    relatedBookingId = input.RelatedBookingId?.ToString(),
                }),
            };
            var auth = context.HttpContext?.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(auth))
                req.Headers.TryAddWithoutValidation("Authorization", auth);
            if (!string.IsNullOrWhiteSpace(input.DeclaredPurpose))
                req.Headers.TryAddWithoutValidation("X-Purpose-Of-Use", input.DeclaredPurpose);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("AI OCR extraction returned {Status}", (int)resp.StatusCode);
                // A 4xx is a real authorization/validation/not-found decision → surface it, do NOT mask it as
                // Available=false (else the gate is silently swallowed and the AI purpose log never fires).
                AiErrorMapper.ThrowIfClientError(resp.StatusCode);
                return null;   // 5xx / other → genuinely unavailable
            }

            var dto = await resp.Content.ReadFromJsonAsync<AiLabReportExtractResponse>(ct);
            if (dto is null) return null;
            return new OcrExtractionResult(
                dto.ExtractionId ?? "",
                dto.OcrEngine ?? "unknown",
                dto.OverallConfidence,
                dto.RequiresHumanReview,
                dto.AbnormalCount,
                (dto.Analytes ?? []).Select(a => new OcrAnalyteResult(
                    a.Test ?? "", a.Value, a.Unit, a.RefLow, a.RefHigh, a.Flag ?? "normal")).ToList(),
                "ai-service-http");
        }
        // Let typed authorization/validation/not-found exceptions escape; only transport/deserialize → unavailable.
        catch (Exception ex) when (ex is not AppExceptionBase and not KeyNotFoundException)
        {
            logger.LogWarning(ex, "AI OCR call failed; extraction unavailable.");   // no PHI in the message
            return null;
        }
    }

    // Matches the AI service's LabReportExtractResponse schema (camelCase). rawTextPreview is INTENTIONALLY not
    // bound — the proxy never surfaces the decrypted OCR text (PHI minimization at the boundary).
    private sealed record AiLabReportExtractResponse(
        string? ExtractionId, string? SourceUrl, string? OcrEngine, double OverallConfidence,
        bool RequiresHumanReview, List<AiAnalyte>? Analytes, int AbnormalCount);
    private sealed record AiAnalyte(string? Test, double Value, string? Unit, double RefLow, double RefHigh, string? Flag);
}

/// <summary>
/// Deterministic DEV/test stub for OCR extraction — a clearly-labelled mock so the endpoint works end-to-end
/// WITHOUT the AI service. It NEVER fabricates a plausible real lab value (the analyte is named "STUB-PANEL"
/// and always flagged for human review) and NEVER returns raw OCR text. The sourceUrl is NEVER logged.
/// Production swaps the HTTP adapter behind the same seam.
/// </summary>
public sealed class StubAiOcrClient : IAiOcrClient
{
    public Task<OcrExtractionResult?> ExtractLabReportAsync(OcrExtractInput input, CancellationToken ct)
    {
        // Deterministic, non-random: derive a stable shape from the patient id so the same input → same output.
        var seed = (int)(BitConverter.ToUInt32(input.RelatedPatientId.ToByteArray(), 0) % 3);
        var analytes = new List<OcrAnalyteResult>
        {
            new("STUB-PANEL", 10 + seed, "g/dL", 12, 16, seed == 0 ? "normal" : "low"),
        };
        return Task.FromResult<OcrExtractionResult?>(new OcrExtractionResult(
            ExtractionId: $"stub-{input.RelatedPatientId:N}",
            OcrEngine: "stub-ocr-v0",
            OverallConfidence: 0.5,
            RequiresHumanReview: true,            // a stub result is NEVER auto-trusted
            AbnormalCount: seed == 0 ? 0 : 1,
            Analytes: analytes,
            Source: "stub-dev"));
    }
}
