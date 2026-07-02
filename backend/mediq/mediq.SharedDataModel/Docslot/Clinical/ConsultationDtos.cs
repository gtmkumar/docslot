namespace mediq.SharedDataModel.Docslot.Clinical;

/// <summary>
/// Consultation-composer DTOs (Phase A of docs/PRESCRIPTION_CONSULTATION_PLAN.md). One consultation record,
/// two roles, one signing transition (draft → finalized). The wire contract is FIXED — the frontend is built
/// against these field names in parallel; do not rename. camelCase JSON.
/// </summary>

/// <summary>Consultation intake vitals — UNENCRYPTED standard PHI (purpose-of-use gated on read, but not in
/// the encrypted_fields_registry). Maps to/from the <c>docslot.prescriptions.vitals</c> JSONB object.</summary>
public sealed record VitalsDto(
    string? Bp = null,          // e.g. "120/80"
    int? PulseBpm = null,
    decimal? TempF = null,
    int? Spo2 = null,
    decimal? WeightKg = null);

/// <summary>Body of <c>POST /consultations</c> — the booking to open (get-or-create) the draft for.</summary>
public sealed record CreateConsultationRequest(Guid BookingId);

/// <summary>
/// The draft consultation returned by <c>POST /consultations</c> — DECRYPTED clinical PHI (consent +
/// purpose-of-use gated upstream; never cached). <c>consultationId</c> IS the prescription_id.
/// </summary>
public sealed record ConsultationDraftDto(
    Guid ConsultationId,
    string? PrescriptionNumber,
    Guid BookingId,
    Guid PatientId,
    string? PatientName,
    string Status,
    VitalsDto Vitals,
    string? ChiefComplaints,          // decrypted
    string? Examination,              // decrypted
    string? Diagnosis,                // decrypted
    string MedicationsJson,           // decrypted — opaque JSON array (encrypted at rest like PrescriptionDto)
    string[] Investigations,
    string? Advice,
    int? FollowUpInDays,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Autosave/save of draft fields. Every field is OPTIONAL — only the provided (non-null) fields are written
/// onto the draft (a null leaves the stored value unchanged), so a partial autosave never wipes other fields.
/// </summary>
public sealed record SaveConsultationRequest(
    VitalsDto? Vitals,
    string? ChiefComplaints,
    string? Examination,
    string? Diagnosis,
    string? MedicationsJson,
    string[]? Investigations,
    string? Advice,
    int? FollowUpInDays);

/// <summary>The doctor's signing act. <c>overrideReason</c> is required only to sign past unoverridden
/// high/critical drug-safety alerts; null/blank otherwise. No doctorId — the signer is server-derived.</summary>
public sealed record FinalizeConsultationRequest(string? OverrideReason);

/// <summary>
/// Result of <c>POST /consultations/{id}/finalize</c>. When <c>Finalized=false</c> the draft was NOT signed
/// because unoverridden high/critical alerts block it (supply <c>overrideReason</c> to proceed); <c>Alerts</c>
/// carries them (medication names → never cached). When <c>Finalized=true</c>, <c>Alerts</c> is empty.
/// </summary>
public sealed record FinalizeConsultationResult(
    bool Finalized,
    Guid PrescriptionId,
    string? PrescriptionNumber,
    IReadOnlyList<DrugAlertDto> Alerts);
