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
    /// feature-building to the AI service and passes only the booking id).
    /// <para><paramref name="serviceBearer"/>: when set (a background worker with NO live caller), the HTTP
    /// adapter sends THIS bearer (a short-lived service token) instead of the request's forwarded JWT. Null on
    /// the on-demand request path (the adapter forwards the caller's JWT via IHttpContextAccessor).</para></summary>
    Task<NoShowRisk?> PredictAsync(Guid bookingId, NoShowFeatures features, string? serviceBearer, CancellationToken ct);
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

/// <summary>
/// The seam to the AI sibling service's OCR document-extraction (<c>POST /ai/v1/extractions/lab-report</c>):
/// extract structured analytes from a lab-report image. The AI service owns <c>ai.*</c> (it persists the
/// extraction + audits + writes the purpose-of-use log); .NET only proxies. On a 5xx / network / timeout /
/// deserialize failure the adapter returns null (the caller surfaces "extraction unavailable"); an AI 4xx
/// (authorization / validation / not-found) is propagated as a typed exception so the gate is NEVER masked as
/// "unavailable". Same honest-stub seam + JWT/purpose forwarding as <see cref="IAiTriageClient"/>.
/// </summary>
public interface IAiOcrClient
{
    /// <summary>Extracts a lab report. Returns null only when the AI service is UNAVAILABLE (5xx/network);
    /// AI 4xx surfaces as ForbiddenException/ValidationException/KeyNotFoundException, not a null.</summary>
    Task<OcrExtractionResult?> ExtractLabReportAsync(OcrExtractInput input, CancellationToken ct);

    /// <summary>Runs OCR on a CALLER-SUPPLIED paper-prescription image, returning transcribable draft records (drug
    /// names etc.) for the paper-Rx intake flow. Unlike the lab extract, the image bytes are provided by the caller
    /// (front desk holds the physical document) — no server-side blob is read. Returns null only when the AI service
    /// is UNAVAILABLE (5xx/network); an AI 4xx surfaces as a typed exception (the gate is never masked).</summary>
    Task<PrescriptionExtractionResult?> ExtractPrescriptionAsync(OcrPrescriptionInput input, CancellationToken ct);

    /// <summary>Lists the tenant's recent extraction SUMMARIES (no analyte values) for the ops/forensics view.
    /// The AI service scopes by the JWT tenant. Returns null when the AI service is UNAVAILABLE (5xx/network);
    /// AI 4xx surfaces as a typed exception.</summary>
    Task<IReadOnlyList<OcrExtractionSummaryResult>?> ListExtractionsAsync(int limit, CancellationToken ct);
}

/// <summary>A paper-prescription OCR request. The caller supplies the image bytes (base64) — the front desk holds
/// the physical Rx — so there is no server blob / client filesystem path to resolve. <paramref name="DeclaredPurpose"/>
/// is the X-Purpose-Of-Use (DPDP) — REQUIRED and forwarded to the AI sibling.</summary>
public sealed record OcrPrescriptionInput(
    Guid RelatedPatientId, string ImageBase64, string ContentType, string FileName, string DeclaredPurpose);

/// <summary>One transcribable draft record OCR'd from a paper prescription (the intake staff verifies/edits before
/// import). <paramref name="Confidence"/> (0..1) is per-record; the whole result is advisory. Title/description are
/// PHI (the response is never cached + is purpose gated).</summary>
public sealed record PrescriptionRecordResult(string RecordType, string Title, string? Description, double? Confidence);

/// <summary>A paper-prescription OCR result (advisory). <paramref name="Source"/> records provenance
/// ('ai-service-http' | 'stub-dev'). <paramref name="RawText"/> is the transcribed text (surfaced for the intake
/// desk to verify against the scan — PHI, so the response is never cached and is purpose gated).</summary>
public sealed record PrescriptionExtractionResult(
    string ExtractionId, double? OverallConfidence, string? ExternalDoctorName, DateOnly? RecordedDate,
    IReadOnlyList<PrescriptionRecordResult> Records, string? RawText, string Source);

/// <summary>An extraction SUMMARY (header only — never the analyte values). Operational metadata for the ops list;
/// <paramref name="AbnormalCount"/> is an aggregate count, not an individual result. <paramref name="Source"/>
/// records provenance ('ai-service-http' | 'stub-dev').</summary>
public sealed record OcrExtractionSummaryResult(
    string ExtractionId, string SourceType, string Status, double? OverallConfidence,
    bool RequiresHumanReview, int AbnormalCount, string CreatedAt, string Source);

/// <summary>An OCR extraction request. No client-supplied source path is carried — the AI service treats a
/// source path as a local filesystem path, so .NET never forwards a client-controlled one (it would be an
/// arbitrary-file-read primitive); the AI uses its generated sample in dev. <paramref name="DeclaredPurpose"/>
/// is the X-Purpose-Of-Use (DPDP) — REQUIRED (the extraction is always patient-bound) and forwarded.</summary>
public sealed record OcrExtractInput(Guid RelatedPatientId, Guid? RelatedBookingId, string DeclaredPurpose);

/// <summary>An extracted analyte. The value is PHI; never logged by the adapter.</summary>
public sealed record OcrAnalyteResult(string Test, double Value, string? Unit, double RefLow, double RefHigh, string Flag);

/// <summary>An OCR extraction result. <paramref name="Source"/> records provenance ('ai-service-http' | 'stub-dev').
/// Deliberately omits the AI's raw OCR text preview (PHI minimization at the proxy boundary).</summary>
public sealed record OcrExtractionResult(
    string ExtractionId, string OcrEngine, double OverallConfidence, bool RequiresHumanReview,
    int AbnormalCount, IReadOnlyList<OcrAnalyteResult> Analytes, string Source);

/// <summary>
/// The seam to the AI sibling service's RAG ask (<c>POST /ai/v1/rag/ask</c>): a natural-language question over
/// a patient's indexed medical history → an answer + citations. STRICTLY READ-ONLY — this interface exposes
/// ONLY <see cref="AskAsync"/>; there is intentionally NO index method, so <c>/rag/index</c> (the read-that-writes
/// path, bug #3) is structurally unreachable from .NET. The AI service governs any external-LLM PHI egress
/// (allows_phi/BAA gate) and writes the purpose-of-use log. Null on 5xx/network; 4xx propagated as typed exceptions.
/// </summary>
public interface IAiRagClient
{
    /// <summary>Asks a question over a patient's history. Returns null only when the AI service is UNAVAILABLE
    /// (5xx/network); AI 4xx surfaces as a typed exception. The question (PHI) is NEVER logged by the adapter.</summary>
    Task<RagAnswerResult?> AskAsync(RagAskInput input, CancellationToken ct);

    /// <summary>The tenant's RAG knowledge-base STATUS (operational counts — embeddings/patients-indexed/KBs, no
    /// PHI). The AI service scopes by the JWT tenant. Null when UNAVAILABLE (5xx/network); AI 4xx → typed exception.</summary>
    Task<RagStatusResult?> GetStatusAsync(CancellationToken ct);
}

/// <summary>A RAG knowledge base's summary counts.</summary>
public sealed record RagKnowledgeBaseResult(string KbKey, string Name, int DocumentCount);

/// <summary>The RAG status (operational, non-PHI). <paramref name="Source"/> records provenance.</summary>
public sealed record RagStatusResult(
    int Embeddings, int PatientsIndexed, IReadOnlyList<RagKnowledgeBaseResult> KnowledgeBases, string Source);

/// <summary>A RAG ask request. <paramref name="Question"/> is free-text PHI; <paramref name="DeclaredPurpose"/> is the
/// X-Purpose-Of-Use (DPDP) — REQUIRED (a RAG ask is always patient-bound) and forwarded to the AI service.</summary>
public sealed record RagAskInput(Guid PatientId, string Question, string DeclaredPurpose);

/// <summary>A citation backing a RAG answer.</summary>
public sealed record RagCitationResult(string HistoryId, string? RecordType, string? Title, string? Severity, double Score);

/// <summary>A RAG answer. <paramref name="Mode"/> is 'extractive' | 'llm'; <paramref name="Source"/> records
/// provenance ('ai-service-http' | 'stub-dev'). The answer is PHI; never logged by the adapter.</summary>
public sealed record RagAnswerResult(
    Guid PatientId, string Answer, string Mode, IReadOnlyList<RagCitationResult> Citations, int Retrieved, string Source);
