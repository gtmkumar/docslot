namespace mediq.SharedDataModel.Docslot.Triage;

/// <summary>A triage request from the intake desk. <see cref="Complaint"/> is free-text symptom data (PHI) —
/// it is sent to the AI sibling service and is never logged/cached by the .NET service.</summary>
public sealed record TriageRequest(
    string Complaint,
    Guid? PatientId = null,
    Guid? BookingId = null,
    int? PatientAge = null);

/// <summary>
/// The AI triage assessment (advisory). <see cref="Available"/> is false when the AI service is unreachable
/// (the desk shows "triage unavailable" rather than a fabricated assessment). <see cref="UrgencyBand"/> is
/// low/medium/high/emergency; <see cref="RedFlags"/> are escalation indicators; <see cref="SuggestedDoctors"/>
/// route the complaint. <see cref="Source"/> records provenance ('ai-service-http' | 'stub-dev').
/// </summary>
public sealed record TriageResultDto(
    bool Available,
    string? UrgencyBand,
    string? Department,
    IReadOnlyList<string> RedFlags,
    IReadOnlyList<string> Symptoms,
    IReadOnlyList<SuggestedDoctorDto> SuggestedDoctors,
    string? RunId,
    string? Source = null);

/// <summary>A doctor the triage workflow suggests for the complaint's department.</summary>
public sealed record SuggestedDoctorDto(
    Guid DoctorId, string FullName, string? Specialization, decimal? ConsultationFee, string? NextAvailableSlot);
