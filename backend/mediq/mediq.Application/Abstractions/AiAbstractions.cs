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

/// <summary>
/// The seam to the AI sibling service's LangGraph TRIAGE workflow (<c>POST /triage</c>): a free-text symptom
/// complaint → an urgency band + red flags + a suggested department/doctors. The complaint is PHI; the HTTP
/// adapter sends it to the AI SIBLING (an internal service-to-service hop, not a third-party disclosure) and the
/// AI service governs any onward external-LLM egress (the allows_phi/BAA gate). A dev stub stands in so the
/// endpoint works without the AI service. Same flag/seam as <see cref="IAiNoShowClient"/>.
/// </summary>
public interface IAiTriageClient
{
    /// <summary>Triages a complaint. Returns null when the AI service is UNAVAILABLE (unreachable / errored) — the
    /// caller surfaces "unavailable" rather than a fabricated assessment. The complaint MUST NOT be logged.</summary>
    Task<TriageResult?> TriageAsync(TriageRequestInput request, CancellationToken ct);
}

/// <summary>A triage request. <paramref name="Complaint"/> is free-text symptom data (PHI).
/// <paramref name="DeclaredPurpose"/> is the X-Purpose-Of-Use (DPDP) — REQUIRED when the request is bound to a
/// patient/booking (the AI service enforces it and writes the purpose-of-use log); forwarded to the AI service.</summary>
public sealed record TriageRequestInput(
    string Complaint, Guid? PatientId, Guid? BookingId, int? PatientAge, string? DeclaredPurpose);

/// <summary>A triage result: urgency band + red flags + suggested department/doctors. <paramref name="Source"/>
/// records provenance (<c>ai-service-http</c> | <c>stub-dev</c>).</summary>
public sealed record TriageResult(
    string? RunId,
    string Department,
    string UrgencyBand,                       // low | medium | high | emergency
    IReadOnlyList<string> RedFlags,
    IReadOnlyList<string> Symptoms,
    IReadOnlyList<SuggestedDoctorResult> SuggestedDoctors,
    string Source);

/// <summary>A doctor the triage workflow suggests for the complaint's department.</summary>
public sealed record SuggestedDoctorResult(
    Guid DoctorId, string FullName, string? Specialization, decimal? ConsultationFee, string? NextAvailableSlot);
