namespace mediq.Application.Abstractions;

/// <summary>
/// The seam to the Python AI sibling service for no-show risk scoring. In production this is an HTTP call to
/// the AI service's <c>POST /predictions/no-show</c> (which fetches the booking, builds features, scores via
/// the trained model, persists to <c>ai.ai_predictions</c>, and returns the probability) — the .NET service is
/// the system of record and does NOT touch the <c>ai.*</c> schema (the AI service owns it). In dev/test a
/// deterministic stub stands in so the endpoint works without the AI service running. Behind this interface so
/// the booking surface is AI-availability-agnostic. Same honest-stub seam as the payout / blob / ABDM rails.
/// </summary>
public interface IAiNoShowClient
{
    /// <summary>Scores a booking's no-show risk. Returns null when the risk is UNAVAILABLE (the AI service is
    /// unreachable / errored) — the caller surfaces "unavailable" rather than fabricating a score. The stub
    /// always returns a score; <paramref name="features"/> feed the stub heuristic (the HTTP adapter delegates
    /// feature-building to the AI service and passes only the booking id).</summary>
    Task<NoShowRisk?> PredictAsync(Guid bookingId, NoShowFeatures features, CancellationToken ct);
}

/// <summary>A minimal, non-PHI feature snapshot the .NET side can derive from a booking for the stub heuristic.</summary>
public sealed record NoShowFeatures(int LeadTimeDays, int SlotHour, bool IsBehalfBooking);

/// <summary>A no-show risk result. <paramref name="Source"/> records provenance (<c>ai-service-http</c> | <c>stub-dev</c>).</summary>
public sealed record NoShowRisk(double Probability, string Band, string ModelName, string Source);
