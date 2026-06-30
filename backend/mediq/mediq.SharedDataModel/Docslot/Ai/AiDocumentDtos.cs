namespace mediq.SharedDataModel.Docslot.Ai;

// ===================================================================================================
// Slice-11 AI document surfaces (OCR lab-report extraction + RAG ask), proxied through the .NET API to
// the Python AI sibling service. Both carry PHI: the .NET service forwards the caller's JWT + the
// declared purpose-of-use to the AI service (which owns ai.*, persists, and writes the purpose log),
// and surfaces strictly LESS PHI than the AI returns (no raw OCR text; the RAG question is not echoed).
// ===================================================================================================

// ---- OCR (lab-report extraction) ------------------------------------------------------------------

/// <summary>Request to extract structured analytes from a lab-report image via the AI sibling service.
/// <see cref="RelatedPatientId"/> is REQUIRED (the extraction is a patient-bound PHI access → an
/// X-Purpose-Of-Use header is required, carried out-of-band, not in this body). NOTE: a client-supplied
/// source path is DELIBERATELY NOT accepted — the AI service treats it as a local filesystem path, so
/// exposing it at the edge would be an arbitrary-file-read primitive (auditor MEDIUM). In dev the AI uses a
/// generated sample; real report-targeting is a pre-prod feature that must resolve a SERVER-controlled blob
/// handle on the AI side (never a raw client path).</summary>
public sealed record ExtractLabReportRequest(
    Guid RelatedPatientId,
    Guid? RelatedBookingId = null);

/// <summary>A single extracted lab analyte. The value IS PHI (lab result) — this response is never cached
/// (the command is IDoNotCacheResponse) and is consent + purpose-of-use gated.</summary>
public sealed record AnalyteDto(
    string Test, double Value, string? Unit, double RefLow, double RefHigh, string Flag);

/// <summary>The AI OCR extraction result (advisory). <see cref="Available"/> is false when the AI service is
/// unreachable/errored (the desk shows "extraction unavailable" rather than fabricating a result); an AI
/// authorization/validation failure surfaces as a 4xx, never as a masked <see cref="Available"/>=false.
/// PHI-MINIMIZED: deliberately OMITS the AI's rawTextPreview (decrypted OCR text). <see cref="Source"/>
/// records provenance ('ai-service-http' | 'stub-dev').</summary>
public sealed record OcrExtractionDto(
    bool Available,
    string? ExtractionId,
    string? OcrEngine,
    double? OverallConfidence,
    bool? RequiresHumanReview,
    int? AbnormalCount,
    IReadOnlyList<AnalyteDto> Analytes,
    string? Source = null);

// ---- RAG (ask over a patient's indexed medical history) -------------------------------------------

/// <summary>A natural-language question over a patient's indexed medical history. The question is PHI — it
/// is forwarded to the AI sibling (a MUTATION variable, never a cache key) and never logged by .NET. The
/// patient id is carried on the route, not this body.</summary>
public sealed record RagAskRequest(string Question);

/// <summary>A citation backing a RAG answer (points at the patient's medical-history record).</summary>
public sealed record RagCitationDto(string HistoryId, string? RecordType, string? Title, string? Severity, double Score);

/// <summary>The AI RAG answer (advisory). <see cref="Available"/> is false when the AI service is
/// unreachable/errored. <see cref="Mode"/> is 'extractive' (local, no external LLM) | 'llm' (an approved,
/// BAA-signed PHI model answered). PHI-MINIMIZED: does NOT echo the question back. <see cref="Answer"/> is
/// PHI → never cached (IDoNotCacheResponse), consent + purpose-of-use gated. <see cref="Source"/> records
/// provenance ('ai-service-http' | 'stub-dev').</summary>
public sealed record RagAnswerDto(
    bool Available,
    Guid PatientId,
    string? Answer,
    string? Mode,
    IReadOnlyList<RagCitationDto> Citations,
    int? Retrieved,
    string? Source = null);

// ---- AI operational reads (extraction list + RAG status) — non-PHI summaries ----------------------

/// <summary>One extraction SUMMARY (header only — never the analyte values). <see cref="AbnormalCount"/> is an
/// aggregate count, not an individual result.</summary>
public sealed record OcrExtractionSummaryDto(
    string ExtractionId, string SourceType, string Status, double? OverallConfidence,
    bool RequiresHumanReview, int AbnormalCount, string CreatedAt);

/// <summary>The tenant's recent OCR extractions (ops/forensics list — summaries only, no PHI analyte values).
/// <see cref="Available"/> is false when the AI service is unreachable; an authorization failure surfaces as
/// 403/422, never a masked false. <see cref="Source"/> records provenance ('ai-service-http' | 'stub-dev').</summary>
public sealed record OcrExtractionListDto(
    bool Available, IReadOnlyList<OcrExtractionSummaryDto> Extractions, string? Source = null);

/// <summary>A RAG knowledge base's summary counts.</summary>
public sealed record RagKnowledgeBaseDto(string KbKey, string Name, int DocumentCount);

/// <summary>The tenant's RAG knowledge-base status (operational counts — embeddings/patients-indexed/KBs; no PHI).
/// <see cref="Available"/> is false when the AI service is unreachable. <see cref="Source"/> records provenance.</summary>
public sealed record RagStatusDto(
    bool Available, int? Embeddings, int? PatientsIndexed,
    IReadOnlyList<RagKnowledgeBaseDto> KnowledgeBases, string? Source = null);
