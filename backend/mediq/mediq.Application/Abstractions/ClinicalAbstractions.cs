using mediq.Domain.Docslot;

namespace mediq.Application.Abstractions;

/// <summary>
/// Clinical PHI repository (prescriptions, lab_reports, medical_history, abdm_records). All access is
/// RLS-protected (the UnitOfWork sets <c>app.tenant_id</c> per tx; the app role is NOBYPASSRLS) AND
/// tenant-scoped in the query. Encrypted fields are stored/returned as CIPHERTEXT envelopes — the handler
/// decrypts on authorized read. Inserts flush immediately so the DB trigger assigns the PRX-/RPT- number.
/// </summary>
public interface IClinicalRepository
{
    Task<string?> AddPrescriptionAsync(Prescription prescription, CancellationToken ct);   // returns prescription_number
    Task<Prescription?> GetPrescriptionAsync(Guid prescriptionId, Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<PrescriptionRow>> ListPrescriptionsAsync(Guid tenantId, Guid patientId, CancellationToken ct);

    Task<string?> AddLabReportAsync(LabReport report, CancellationToken ct);               // returns report_number
    Task<LabReport?> GetLabReportAsync(Guid reportId, Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<LabReportListRow>> ListLabReportsAsync(Guid tenantId, Guid patientId, CancellationToken ct);
    /// <summary>Marks a report delivered (status→delivered, delivered_at=now). Returns the new (status, deliveredAt) or null if not found.</summary>
    Task<(string Status, DateTime? DeliveredAt)?> DeliverLabReportAsync(Guid reportId, Guid tenantId, CancellationToken ct);

    Task<IReadOnlyList<MedicalHistory>> ListMedicalHistoryAsync(Guid tenantId, Guid patientId, CancellationToken ct);
    /// <summary>Inserts a medical-history record (title/description already ciphertext). Returns the new history_id.</summary>
    Task<Guid> AddMedicalHistoryAsync(MedicalHistory history, CancellationToken ct);
    /// <summary>Fetches one record (existence + patient_id for the encryption context) within the tenant, or null.</summary>
    Task<MedicalHistory?> GetMedicalHistoryAsync(Guid historyId, Guid tenantId, CancellationToken ct);
    /// <summary>
    /// Updates a record in place (title/description already ciphertext) within the tenant. Returns true if a
    /// row matched (history_id + tenant_id). No physical delete — set <paramref name="isActive"/>=false to retire.
    /// </summary>
    Task<bool> UpdateMedicalHistoryAsync(
        Guid historyId, Guid tenantId, string recordType, string titleEnc, string? descEnc,
        string? severity, string? icd10Code, DateOnly? startedDate, DateOnly? endedDate,
        bool isActive, bool isCritical, CancellationToken ct);

    Task AddAbdmRecordAsync(AbdmHealthRecord record, CancellationToken ct);
    Task<AbdmHealthRecord?> GetAbdmRecordAsync(Guid recordId, Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<AbdmHealthRecord>> ListAbdmRecordsAsync(Guid tenantId, Guid patientId, CancellationToken ct);

    /// <summary>
    /// Clinical-access context for a patient in a tenant: masked phone + general-clinical consent state
    /// (patients.consent_given_at) + ABDM consent state/expiry. No PHI beyond the masked phone.
    /// </summary>
    Task<ConsentContextRow?> GetConsentContextAsync(Guid tenantId, Guid patientId, CancellationToken ct);
}

/// <summary>Consent-context projection (masked at the infra seam). Drives the clinical-tab gating UI.</summary>
public sealed record ConsentContextRow(
    Guid PatientId, string MaskedPhone, bool ClinicalConsentActive, bool AbdmConsentActive, DateTime? AbdmConsentExpiresAt);

/// <summary>Lightweight projection of a prescription header (ciphertext fields decrypted by the handler).</summary>
public sealed record PrescriptionRow(Guid PrescriptionId, string? PrescriptionNumber, Guid PatientId, Guid DoctorId, string Status, DateTime CreatedAt);

/// <summary>Lab-report list header (NO clinical content — the structured results are not selected/decrypted).</summary>
public sealed record LabReportListRow(Guid ReportId, string? ReportNumber, string TestName, string Status, bool HasCriticalFindings, DateTime CreatedAt);

/// <summary>
/// ABDM consent gate (<c>docslot.abdm_consents</c>). An ABDM record read/push requires an ACTIVE granted,
/// unexpired consent for the patient in the requesting tenant; otherwise the operation is denied.
/// </summary>
public interface IAbdmConsentService
{
    Task<bool> HasActiveConsentAsync(Guid patientId, Guid requestingTenantId, CancellationToken ct);
    Task<Guid?> GetActiveConsentIdAsync(Guid patientId, Guid requestingTenantId, CancellationToken ct);
}

/// <summary>
/// Column-level access policy awareness (<c>platform.access_policies</c>): does the caller's permission set
/// satisfy the policy gating a (schema.table.column)? Used for defense-in-depth alongside RLS + purpose.
/// </summary>
public interface IAccessPolicyService
{
    /// <summary>True if the caller (with these permission keys) is allowed the column/table per access_policies.</summary>
    Task<bool> IsAllowedAsync(string schema, string table, string? column, IReadOnlySet<string> callerPermissions, CancellationToken ct);
}
