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

public sealed record PrescriptionDto(
    Guid PrescriptionId,
    string? PrescriptionNumber,
    Guid PatientId,
    Guid DoctorId,
    string? ChiefComplaints,        // decrypted
    string? Examination,            // decrypted
    string? Diagnosis,              // decrypted
    string MedicationsJson,         // decrypted
    string? Advice,
    int? FollowUpInDays,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record PrescriptionListItemDto(
    Guid PrescriptionId, string? PrescriptionNumber, Guid DoctorId, string Status, DateTimeOffset CreatedAt);

// ---- Lab reports ---------------------------------------------------------------------------------

public sealed record UploadLabReportRequest(
    Guid BookingId, Guid PatientId, Guid? TestId, string? FileName, string? StructuredResultsJson, bool HasCriticalFindings);

public sealed record UploadLabReportResult(Guid ReportId, string? ReportNumber);

public sealed record LabReportDto(
    Guid ReportId, string? ReportNumber, Guid PatientId, Guid? TestId, string? FileName,
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
    bool IsActive, bool IsCritical, DateTimeOffset AddedAt);

// ---- ABDM health records (consent-gated) ---------------------------------------------------------

public sealed record PushAbdmRecordRequest(
    Guid PatientId, Guid? BookingId, string AbhaNumber, string RecordType, string FhirBundleJson);

public sealed record PushAbdmRecordResult(Guid RecordId);

public sealed record AbdmRecordDto(
    Guid RecordId, Guid PatientId, string AbhaNumber, string RecordType,
    string FhirBundleJson,           // decrypted
    bool IsLinkedToPhr, DateTimeOffset CreatedAt);

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
