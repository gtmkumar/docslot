using System.Globalization;
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

    public async Task<PrescriptionExtractionResult?> ExtractPrescriptionAsync(OcrPrescriptionInput input, CancellationToken ct)
    {
        try
        {
            // The caller supplies the image bytes (front desk holds the physical Rx) → forward them as base64. Same
            // JWT + X-Purpose-Of-Use forwarding as the lab extract; the AI service owns persistence + the purpose log.
            using var req = new HttpRequestMessage(HttpMethod.Post, "/ai/v1/extractions/prescription")
            {
                Content = JsonContent.Create(new
                {
                    relatedPatientId = input.RelatedPatientId.ToString(),
                    imageBase64 = input.ImageBase64,
                    contentType = input.ContentType,
                    fileName = input.FileName,
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
                logger.LogWarning("AI prescription OCR returned {Status}", (int)resp.StatusCode);
                AiErrorMapper.ThrowIfClientError(resp.StatusCode);   // 4xx = real gate decision → surface, don't mask
                return null;
            }

            var dto = await resp.Content.ReadFromJsonAsync<AiPrescriptionExtractResponse>(ct);
            if (dto is null) return null;
            return new PrescriptionExtractionResult(
                dto.ExtractionId ?? "",
                dto.OverallConfidence,
                dto.ExternalDoctorName,
                DateOnly.TryParse(dto.RecordedDate, CultureInfo.InvariantCulture, out var rd) ? rd : null,
                (dto.Records ?? []).Select(r => new PrescriptionRecordResult(
                    r.RecordType ?? "medication", r.Title ?? "", r.Description, r.Confidence)).ToList(),
                dto.RawText,
                "ai-service-http");
        }
        catch (Exception ex) when (ex is not AppExceptionBase and not KeyNotFoundException)
        {
            logger.LogWarning(ex, "AI prescription-OCR call failed; extraction unavailable.");   // no PHI in the message
            return null;
        }
    }

    public async Task<IReadOnlyList<OcrExtractionSummaryResult>?> ListExtractionsAsync(int limit, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/ai/v1/extractions?limit={limit}");
            var auth = context.HttpContext?.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(auth))
                req.Headers.TryAddWithoutValidation("Authorization", auth);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("AI extraction list returned {Status}", (int)resp.StatusCode);
                AiErrorMapper.ThrowIfClientError(resp.StatusCode);
                return null;
            }

            var dto = await resp.Content.ReadFromJsonAsync<AiExtractionListResponse>(ct);
            if (dto is null) return null;
            return (dto.Extractions ?? []).Select(e => new OcrExtractionSummaryResult(
                e.ExtractionId ?? "", e.SourceType ?? "unknown", e.Status ?? "unknown",
                e.OverallConfidence, e.RequiresHumanReview, e.AbnormalCount, e.CreatedAt ?? "", "ai-service-http")).ToList();
        }
        catch (Exception ex) when (ex is not AppExceptionBase and not KeyNotFoundException)
        {
            logger.LogWarning(ex, "AI extraction-list call failed; list unavailable.");
            return null;
        }
    }

    // Matches the AI service's LabReportExtractResponse schema (camelCase). rawTextPreview is INTENTIONALLY not
    // bound — the proxy never surfaces the decrypted OCR text (PHI minimization at the boundary).
    private sealed record AiLabReportExtractResponse(
        string? ExtractionId, string? SourceUrl, string? OcrEngine, double OverallConfidence,
        bool RequiresHumanReview, List<AiAnalyte>? Analytes, int AbnormalCount);
    private sealed record AiAnalyte(string? Test, double Value, string? Unit, double RefLow, double RefHigh, string? Flag);

    // Matches the AI service's ExtractionListResponse / ExtractionListItem schema (camelCase) — summaries only.
    private sealed record AiExtractionListResponse(int Count, List<AiExtractionListItem>? Extractions);
    private sealed record AiExtractionListItem(
        string? ExtractionId, string? SourceType, string? Status, double? OverallConfidence,
        bool RequiresHumanReview, int AbnormalCount, string? CreatedAt);

    // Matches the AI service's PrescriptionExtractResponse schema (camelCase). Unlike the lab response, rawText IS
    // bound — the intake desk needs the transcription to verify against the scan (still PHI: never cached/logged).
    private sealed record AiPrescriptionExtractResponse(
        string? ExtractionId, double? OverallConfidence, string? ExternalDoctorName, string? RecordedDate,
        List<AiPrescriptionRecord>? Records, string? RawText);
    private sealed record AiPrescriptionRecord(string? RecordType, string? Title, string? Description, double? Confidence);
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

    public Task<PrescriptionExtractionResult?> ExtractPrescriptionAsync(OcrPrescriptionInput input, CancellationToken ct) =>
        // Deterministic, clearly-labelled parse — ONE medication record — so the intake flow works end-to-end WITHOUT
        // the AI service. Never fabricates a plausible real drug; the intake desk verifies before importing.
        Task.FromResult<PrescriptionExtractionResult?>(new PrescriptionExtractionResult(
            ExtractionId: $"stub-rx-{input.RelatedPatientId:N}",
            OverallConfidence: 0.5,
            ExternalDoctorName: "Dr. Stub",
            RecordedDate: null,
            Records: new List<PrescriptionRecordResult>
            {
                new("medication", "STUB-MED 500", "1-0-1 · stub parse (verify before import)", 0.5),
            },
            RawText: "STUB PRESCRIPTION OCR — one medication record",
            Source: "stub-dev"));

    public Task<IReadOnlyList<OcrExtractionSummaryResult>?> ListExtractionsAsync(int limit, CancellationToken ct) =>
        // One clearly-labelled stub summary so the ops list renders WITHOUT the AI service. Deterministic.
        Task.FromResult<IReadOnlyList<OcrExtractionSummaryResult>?>(new List<OcrExtractionSummaryResult>
        {
            new("stub-extraction", "lab_report", "completed", 0.5, RequiresHumanReview: true, AbnormalCount: 0,
                CreatedAt: "1970-01-01T00:00:00Z", Source: "stub-dev"),
        });
}
