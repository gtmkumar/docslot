namespace mediq.SharedDataModel.Docslot.Clinical;

/// <summary>
/// Clinical PHI DTOs (slice 03b). Decrypted shapes returned ONLY to authorized callers (RLS + purpose-of-use
/// + consent gates upstream). Encrypted at rest; never logged. Human IDs (PRX-/RPT-) come from DB triggers.
/// </summary>

// ---- Prescriptions -------------------------------------------------------------------------------

public sealed record IssuePrescriptionRequest(
    Guid BookingId,
    Guid PatientId,
    Guid DoctorId,
    string? ChiefComplaints,
    string? Examination,
    string? Diagnosis,
    string MedicationsJson,        // JSON array of {name, dose, frequency, duration}
    string? Advice,
    int? FollowUpInDays);

public sealed record IssuePrescriptionResult(Guid PrescriptionId, string? PrescriptionNumber);

// Amend an issued prescription: new clinical content + a mandatory reason. Targets an existing
// prescription by id (route); mints a new row that supersedes it (the original is marked 'amended').
public sealed record AmendPrescriptionRequest(
    string? ChiefComplaints,
    string? Examination,
    string? Diagnosis,
    string MedicationsJson,
    string? Advice,
    int? FollowUpInDays,
    string AmendmentReason);

public sealed record AmendPrescriptionResult(
    Guid PrescriptionId, string? PrescriptionNumber, Guid SupersededPrescriptionId);

public sealed record PrescriptionDto(
    Guid PrescriptionId,
    string? PrescriptionNumber,
    Guid PatientId,
    Guid DoctorId,
    string? DoctorName,             // read-only presentation; joined from docslot.doctors (plaintext directory data, NOT PHI)
    string? ChiefComplaints,        // decrypted
    string? Examination,            // decrypted
    string? Diagnosis,              // decrypted
    string MedicationsJson,         // decrypted
    string? Advice,
    int? FollowUpInDays,
    string Status,
    Guid? SupersedesPrescriptionId, // amendment lineage (NULL = original); status='amended' = superseded
    DateTimeOffset CreatedAt,
    // Additive (consultation flow) — safe for existing consumers: vitals (unencrypted standard PHI) + the
    // server-derived signer identity/timestamp. Null on legacy rows that predate the consultation columns.
    VitalsDto? Vitals = null,
    Guid? FinalizedByUserId = null,
    DateTimeOffset? FinalizedAt = null);

public sealed record PrescriptionListItemDto(
    Guid PrescriptionId, string? PrescriptionNumber, Guid DoctorId, string? DoctorName, string Status, DateTimeOffset CreatedAt);

/// <summary>A drug-safety alert generated for a prescription (allergy / interaction / duplicate / ...).
/// medication_name is the just-prescribed drug; the conflicting allergen/current-med detail stays behind
/// encryption (linked internally). Surfaced read-side as a clinical-safety banner on the prescription.</summary>
public sealed record DrugAlertDto(
    Guid AlertId,
    string AlertType,               // allergy | interaction | contraindication | duplicate | pregnancy_warning | dosage
    string Severity,                // low | moderate | high | critical
    string MedicationName,
    string Description,
    bool Overridden,
    DateTimeOffset CreatedAt);

// ---- Lab reports ---------------------------------------------------------------------------------

public sealed record UploadLabReportRequest(
    Guid BookingId, Guid PatientId, Guid? TestId, string? FileName, string? StructuredResultsJson, bool HasCriticalFindings);

public sealed record UploadLabReportResult(Guid ReportId, string? ReportNumber);

// Attach the PHI artifact (PDF/image) to a lab report. Content is base64 in the JSON body (the internal
// storage seam; a multipart/presigned-upload path is the prod follow-up for large files).
public sealed record SetLabReportFileRequest(string FileName, string ContentType, string ContentBase64);

public sealed record SetLabReportFileResult(Guid ReportId, long SizeBytes);

// The decrypted file streamed back by the consent-gated download endpoint (never serialized as JSON).
public sealed record LabReportFileDto(Guid ReportId, string FileName, string ContentType, byte[] Content);

public sealed record LabReportDto(
    Guid ReportId, string? ReportNumber, Guid PatientId, Guid? TestId,
    string? TestName,                // read-only presentation; joined from docslot.test_catalog (plaintext catalog data, NOT PHI)
    string? FileName,
    string? StructuredResultsJson,   // decrypted
    string Status, bool HasCriticalFindings, DateTimeOffset CreatedAt);

/// <summary>List row — NO clinical content (headers only). Mirrors FE LabReportListItemSchema.</summary>
public sealed record LabReportListItemDto(
    Guid ReportId, string? ReportNumber, string TestName, string Status, bool HasCriticalFindings, DateTimeOffset CreatedAt);

/// <summary>Result of the lab-report deliver action. Mirrors the booking/clinical action-result shape.</summary>
public sealed record DeliverLabReportResult(Guid ReportId, string Status, DateTimeOffset? DeliveredAt);

// ---- Medical history -----------------------------------------------------------------------------

public sealed record MedicalHistoryDto(
    Guid HistoryId, string RecordType, string Title, string? Description,   // title/description decrypted
    string? Severity, string? Icd10Code, DateOnly? StartedDate, DateOnly? EndedDate,   // non-encrypted scalars — surfaced so an edit round-trips losslessly
    bool IsActive, bool IsCritical, DateTimeOffset AddedAt,
    // Paper-import provenance/verification. source distinguishes clinic vs imported (paper_prescription /
    // patient_reported); externalDoctorName is DECRYPTED (this endpoint is read-permission + purpose gated);
    // verifiedAt NULL ⇒ still an unverified draft; attachment* are surfaced so the UI can offer the scan download.
    string Source = "clinic",
    string? ExternalDoctorName = null,
    DateOnly? RecordedDate = null,
    DateTimeOffset? VerifiedAt = null,
    Guid? ImportBatchId = null,
    string? AttachmentFileName = null,
    string? AttachmentMimeType = null);

// ---- Paper-prescription / external-history import (front-desk intake → UNVERIFIED drafts) ---------

/// <summary>The scanned source document (photo of the paper Rx) — base64 in the JSON body (internal storage
/// seam; a multipart/presigned path is the prod follow-up for large files). Stored encrypted-at-rest.</summary>
public sealed record ImportAttachment(string FileName, string ContentType, string ContentBase64);

/// <summary>One transcribed record within an import batch. title/description are encrypted at rest by the handler.</summary>
public sealed record ImportMedicalHistoryRecord(
    string RecordType,                 // allergy | chronic_condition | surgery | medication | vaccination | family_history | lifestyle
    string Title,
    string? Description,
    string? Severity,                  // mild | moderate | severe | critical
    bool? IsCritical,
    DateOnly? StartedDate);

/// <summary>Import a patient's paper prescription / self-reported history as UNVERIFIED external records. source
/// MUST be external ('clinic' rejected). All records share one generated import batch + the attachment pointer;
/// externalDoctorName + each title/description are encrypted at rest. PatientId comes from the route.</summary>
public sealed record ImportMedicalHistoryRequest(
    string Source,                     // paper_prescription | patient_reported
    string? ExternalDoctorName,
    DateOnly? RecordedDate,
    ImportAttachment? Attachment,
    IReadOnlyList<ImportMedicalHistoryRecord> Records);

/// <summary>Result of an import — the batch id + the created (unverified) history ids. NO PHI echoed back.</summary>
public sealed record ImportMedicalHistoryResult(Guid ImportBatchId, IReadOnlyList<Guid> HistoryIds);

/// <summary>The decrypted scanned-document attachment streamed back by the consent-gated download endpoint
/// (never serialized as JSON).</summary>
public sealed record MedicalHistoryAttachmentDto(Guid HistoryId, string FileName, string ContentType, byte[] Content);

// ---- Unified patient timeline (merged, purpose-gated PHI read-model) ------------------------------

/// <summary>The patient strip that heads the timeline — low-sensitivity relationship facts (NO clinical PHI):
/// when the patient first visited this tenant (first booking) and how many non-cancelled visits.</summary>
public sealed record PatientTimelineStripDto(DateOnly? PatientSince, int VisitCount);

/// <summary>A backend-driven category chip: bilingual label + item count. Included iff the caller holds the
/// category's read permission (count 0 chips are still returned) — the frontend renders chips purely from this
/// list (no client-side role logic).</summary>
public sealed record TimelineCategoryDto(string Key, string LabelEn, string LabelHi, int Count);

/// <summary>Points a timeline item at the row/aggregate its detail panel opens. <c>Type</c> is
/// prescription | lab_report | medical_history | medical_history_batch; <c>Id</c> is the source row id (or the
/// import_batch_id for a document card).</summary>
public sealed record TimelineRefDto(string Type, Guid Id);

/// <summary>One merged timeline entry. <c>Title</c>/<c>Subtitle</c>/<c>Summary</c> are decrypted where the source
/// is PHI (in-policy — the whole read is permission + purpose + consent gated). <c>Summary</c> never lists drug
/// names (only counts). <c>Tags</c> are non-PHI tokens (human id, status, 'critical'). <c>Unverified</c> flags an
/// external record a clinician has not confirmed.</summary>
public sealed record TimelineItemDto(
    Guid ItemId,
    string Category,                 // prescription | lab_report | vaccination | document
    DateTimeOffset OccurredAt,
    string Title,
    string? Subtitle,
    string? Summary,
    IReadOnlyList<string> Tags,
    bool Unverified,
    bool HasAttachment,
    TimelineRefDto Ref);

/// <summary>The unified patient timeline: the header strip, the permitted category chips (with counts), and the
/// merged items sorted most-recent-first (capped). Categories the caller cannot read are omitted entirely.</summary>
public sealed record PatientTimelineDto(
    PatientTimelineStripDto Patient,
    IReadOnlyList<TimelineCategoryDto> Categories,
    IReadOnlyList<TimelineItemDto> Items);

/// <summary>Create a medical-history record. title/description are encrypted at rest by the handler. PatientId comes from the route.</summary>
public sealed record CreateMedicalHistoryRequest(
    string RecordType,                 // allergy | chronic_condition | surgery | medication | vaccination | family_history | lifestyle
    string Title,
    string? Description,
    string? Severity,                  // mild | moderate | severe | critical
    string? Icd10Code,
    DateOnly? StartedDate,
    DateOnly? EndedDate,
    bool IsCritical);

public sealed record CreateMedicalHistoryResult(Guid HistoryId);

/// <summary>Update a medical-history record in place. Set IsActive=false to retire it (no physical delete).</summary>
public sealed record UpdateMedicalHistoryRequest(
    string RecordType,
    string Title,
    string? Description,
    string? Severity,
    string? Icd10Code,
    DateOnly? StartedDate,
    DateOnly? EndedDate,
    bool IsActive,
    bool IsCritical);

// ---- ABDM health records (consent-gated) ---------------------------------------------------------

public sealed record PushAbdmRecordRequest(
    Guid PatientId, Guid? BookingId, string AbhaNumber, string RecordType, string FhirBundleJson);

public sealed record PushAbdmRecordResult(Guid RecordId);

public sealed record AbdmRecordDto(
    Guid RecordId, Guid PatientId, string AbhaNumber, string RecordType,
    int FhirResourceCount,           // count of FHIR resources only — the decrypted bundle is NEVER sent to the client (issue #54)
    bool IsLinkedToPhr,
    string? CareContextId,           // ABDM network linkage reference (set once published to the national network)
    DateTimeOffset CreatedAt);

/// <summary>Result of linking a stored ABDM record's care context to the national network (HIP data push).</summary>
public sealed record LinkAbdmRecordResult(Guid RecordId, bool Linked, string? CareContextId, string Provider);

/// <summary>List row — NO clinical content (headers only). Mirrors FE AbdmRecordListItemSchema.</summary>
public sealed record AbdmRecordListItemDto(
    Guid RecordId, string RecordType, string AbhaNumber, bool IsLinkedToPhr, DateTimeOffset CreatedAt);

// ---- Patient clinical-access context (consent status) --------------------------------------------

/// <summary>
/// A patient's clinical-access context — consent state for general PHI + ABDM. Mirrors FE
/// PatientConsentSchema. NO PHI beyond a masked phone. Used to drive the clinical-tab gating UI.
/// </summary>
public sealed record PatientConsentDto(
    Guid PatientId,
    string MaskedPhone,
    string ClinicalConsent,          // granted | revoked (derived from patients.consent_given_at)
    string AbdmConsent,              // granted | revoked (derived from abdm_consents active state)
    DateTimeOffset? ConsentExpiresAt);
