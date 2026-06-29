using System.Net.Http.Json;
using mediq.Application.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace mediq.Infrastructure.Ai;

/// <summary>
/// Calls the AI sibling service's <c>POST /triage</c> LangGraph workflow. The complaint (PHI) is sent to the AI
/// SIBLING (internal service-to-service) and the caller's bearer JWT is forwarded (the AI service validates the
/// same DocSlot JWT). On ANY failure it returns null so the caller surfaces "triage unavailable" rather than
/// fabricating. The complaint is NEVER logged here; the AI service governs any onward external-LLM egress.
/// </summary>
public sealed class HttpAiTriageClient(
    HttpClient http, IHttpContextAccessor context, ILogger<HttpAiTriageClient> logger) : IAiTriageClient
{
    public async Task<TriageResult?> TriageAsync(TriageRequestInput request, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/triage")
            {
                Content = JsonContent.Create(new
                {
                    complaint = request.Complaint,
                    patientId = request.PatientId?.ToString(),
                    bookingId = request.BookingId?.ToString(),
                    patientAge = request.PatientAge,
                }),
            };
            var auth = context.HttpContext?.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(auth))
                req.Headers.TryAddWithoutValidation("Authorization", auth);
            // Forward the declared purpose-of-use so the AI service's DPDP gate + purpose log fire on the
            // patient/booking-bound path (the .NET validator already required it there).
            if (!string.IsNullOrWhiteSpace(request.DeclaredPurpose))
                req.Headers.TryAddWithoutValidation("X-Purpose-Of-Use", request.DeclaredPurpose);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Log status ONLY — never the complaint (PHI).
                logger.LogWarning("AI triage returned {Status}", (int)resp.StatusCode);
                return null;
            }

            var dto = await resp.Content.ReadFromJsonAsync<AiTriageResponse>(ct);
            if (dto is null) return null;
            return new TriageResult(
                dto.RunId,
                dto.Department ?? "General Medicine",
                dto.Urgency?.Band ?? "low",
                dto.Urgency?.RedFlags ?? [],
                dto.Symptoms ?? [],
                (dto.SuggestedDoctors ?? []).Select(d => new SuggestedDoctorResult(
                    Guid.TryParse(d.DoctorId, out var id) ? id : Guid.Empty,
                    d.FullName ?? "", d.Specialization, d.ConsultationFee, d.NextAvailableSlot)).ToList(),
                "ai-service-http");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI triage call failed; triage unavailable.");   // no complaint in the message
            return null;
        }
    }

    // Matches the AI service's TriageResponse schema (camelCase).
    private sealed record AiTriageResponse(
        string? RunId, string? WorkflowKey, List<string>? Symptoms, string? Department,
        AiUrgency? Urgency, List<AiSuggestedDoctor>? SuggestedDoctors);
    private sealed record AiUrgency(string? Band, List<string>? RedFlags);
    private sealed record AiSuggestedDoctor(
        string? DoctorId, string? FullName, string? Specialization, decimal? ConsultationFee, string? NextAvailableSlot);
}

/// <summary>
/// Deterministic DEV/test stub for triage — a small, clearly-labelled keyword classifier over the complaint that
/// returns an urgency band + red flags + a routed department, so the triage endpoint works end-to-end WITHOUT the
/// AI service running. NOT the LangGraph workflow and NOT a substitute; it suggests no doctors (no model/DB). The
/// complaint is processed in-memory only and NEVER logged. Production swaps the HTTP adapter behind the same seam.
/// </summary>
public sealed class StubAiTriageClient : IAiTriageClient
{
    private static readonly (string Keyword, string Flag)[] Emergency =
    {
        ("chest pain", "possible cardiac event"), ("breathless", "respiratory distress"),
        ("shortness of breath", "respiratory distress"), ("short of breath", "respiratory distress"),
        ("difficulty breathing", "respiratory distress"), ("unconscious", "loss of consciousness"),
        ("unresponsive", "loss of consciousness"), ("severe bleeding", "haemorrhage"),
        ("stroke", "possible stroke"), ("seizure", "seizure"), ("not breathing", "respiratory arrest"),
        ("cardiac arrest", "cardiac arrest"), ("fainting", "syncope"),
    };
    private static readonly string[] High =
    {
        "high fever", "severe pain", "vomiting blood", "blood in stool", "persistent vomiting", "dehydration",
    };

    public Task<TriageResult?> TriageAsync(TriageRequestInput request, CancellationToken ct)
    {
        var c = request.Complaint.ToLowerInvariant();
        var redFlags = Emergency.Where(e => c.Contains(e.Keyword, StringComparison.Ordinal)).Select(e => e.Flag).Distinct().ToList();

        var band = redFlags.Count > 0 ? "emergency"
            : High.Any(k => c.Contains(k, StringComparison.Ordinal)) ? "high"
            : "low";

        var department =
            c.Contains("chest", StringComparison.Ordinal) || c.Contains("cardiac", StringComparison.Ordinal) || c.Contains("palpitation", StringComparison.Ordinal) ? "Cardiology"
            : c.Contains("breath", StringComparison.Ordinal) || c.Contains("cough", StringComparison.Ordinal) || c.Contains("wheez", StringComparison.Ordinal) ? "Pulmonology"
            : c.Contains("rash", StringComparison.Ordinal) || c.Contains("skin", StringComparison.Ordinal) ? "Dermatology"
            : "General Medicine";

        var symptoms = Emergency.Select(e => e.Keyword).Concat(High)
            .Where(k => c.Contains(k, StringComparison.Ordinal)).Distinct().ToList();

        return Task.FromResult<TriageResult?>(new TriageResult(
            RunId: null, Department: department, UrgencyBand: band, RedFlags: redFlags,
            Symptoms: symptoms, SuggestedDoctors: [], Source: "stub-dev"));
    }
}
