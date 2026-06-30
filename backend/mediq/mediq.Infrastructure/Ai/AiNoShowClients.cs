using System.Net.Http.Json;
using mediq.Application.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace mediq.Infrastructure.Ai;

/// <summary>
/// Calls the Python AI service's <c>POST /predictions/no-show</c> to score a booking. It sends ONLY the booking
/// id (the AI service fetches the booking + builds features itself, the authoritative path) and FORWARDS the
/// caller's bearer JWT (the AI service validates the same DocSlot JWT). On any failure (unreachable, timeout,
/// non-2xx) it returns null so the booking read surfaces "risk unavailable" rather than failing or fabricating
/// — the AI service is an advisory sibling, never on the critical path. NO PHI is sent (only the booking id).
/// </summary>
public sealed class HttpAiNoShowClient(
    HttpClient http, IHttpContextAccessor context, ILogger<HttpAiNoShowClient> logger) : IAiNoShowClient
{
    public async Task<NoShowRisk?> PredictAsync(Guid bookingId, NoShowFeatures features, string? serviceBearer, CancellationToken ct)
    {
        try
        {
            // Full path incl. the AI service's api_prefix (/ai/v1); BaseUrl is the host root. (A bare
            // "/predictions/no-show" would 404 against the prefix-mounted FastAPI routers.)
            using var req = new HttpRequestMessage(HttpMethod.Post, "/ai/v1/predictions/no-show")
            {
                Content = JsonContent.Create(new { bookingId = bookingId.ToString() }),
            };
            // A worker passes a short-lived SERVICE token (no HttpContext); the on-demand path forwards the
            // caller's JWT from the request. Prefer the explicit service bearer when provided.
            var auth = !string.IsNullOrWhiteSpace(serviceBearer)
                ? $"Bearer {serviceBearer}"
                : context.HttpContext?.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrWhiteSpace(auth))
                req.Headers.TryAddWithoutValidation("Authorization", auth);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("AI no-show prediction for {BookingId} returned {Status}", bookingId, (int)resp.StatusCode);
                return null;
            }

            var dto = await resp.Content.ReadFromJsonAsync<AiNoShowResponse>(ct);
            if (dto is null) return null;
            return new NoShowRisk(dto.NoShowProbability, dto.RiskBand ?? "low", dto.ModelName ?? "ai-service", "ai-service-http");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI no-show prediction for {BookingId} failed; risk unavailable.", bookingId);
            return null;
        }
    }

    // Matches the AI service's NoShowPrediction schema (camelCase; web defaults bind these).
    private sealed record AiNoShowResponse(string? BookingId, double NoShowProbability, string? RiskBand, string? ModelName, string? ModelVersion);
}

/// <summary>
/// Deterministic DEV/test stub for no-show scoring — a small, clearly-labelled heuristic over non-PHI booking
/// features (lead time, slot hour, on-behalf), so the no-show-risk endpoint works end-to-end WITHOUT the AI
/// service running. NOT the trained model and NOT a substitute for it; production swaps the HTTP adapter behind
/// the same <see cref="IAiNoShowClient"/> seam. Pure/deterministic.
/// </summary>
public sealed class StubAiNoShowClient : IAiNoShowClient
{
    public Task<NoShowRisk?> PredictAsync(Guid bookingId, NoShowFeatures f, string? serviceBearer, CancellationToken ct)
    {
        // serviceBearer is irrelevant to the in-memory stub (no HTTP); the worker still passes one in http mode.
        // Conservative, monotone heuristic: longer lead time, off-hours slots, and on-behalf bookings raise risk.
        var p = 0.10
            + (f.LeadTimeDays > 14 ? 0.18 : f.LeadTimeDays > 7 ? 0.10 : f.LeadTimeDays >= 3 ? 0.04 : 0.0)
            + (f.SlotHour < 9 || f.SlotHour >= 18 ? 0.12 : 0.0)
            + (f.IsBehalfBooking ? 0.05 : 0.0);
        p = Math.Clamp(Math.Round(p, 4), 0.02, 0.95);
        var band = p < 0.20 ? "low" : p < 0.50 ? "medium" : "high";
        return Task.FromResult<NoShowRisk?>(new NoShowRisk(p, band, "stub-heuristic-v1", "stub-dev"));
    }
}
