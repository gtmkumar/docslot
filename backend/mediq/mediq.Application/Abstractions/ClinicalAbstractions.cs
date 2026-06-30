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
    /// <summary>Detail read: the entity PLUS the joined doctor name (plaintext directory data, NOT PHI).
    /// Callers that only need the entity (amend, drug-alerts) unwrap <c>.Prescription</c>.</summary>
    Task<PrescriptionDetail?> GetPrescriptionAsync(Guid prescriptionId, Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<PrescriptionRow>> ListPrescriptionsAsync(Guid tenantId, Guid patientId, CancellationToken ct);
    /// <summary>Conditionally marks an amendable prescription (status finalized/delivered) as 'amended'.
    /// Returns false if it was not in an amendable state (already amended / draft / cross-tenant) — the
    /// single-winner guard for a concurrent amend.</summary>
    Task<bool> MarkPrescriptionSupersededAsync(Guid prescriptionId, Guid tenantId, CancellationToken ct);

    Task<string?> AddLabReportAsync(LabReport report, CancellationToken ct);               // returns report_number
    /// <summary>Detail read: the entity PLUS the joined test name (plaintext catalog data, NOT PHI).
    /// Callers that only need the entity (file upload/download) unwrap <c>.Report</c>.</summary>
    Task<LabReportDetail?> GetLabReportAsync(Guid reportId, Guid tenantId, CancellationToken ct);
    /// <summary>Attaches (or replaces) the stored PHI artifact reference (blob key + file metadata) on a
    /// tenant-scoped report; pending → ready. Returns false if the report was not found in this tenant.</summary>
    Task<bool> SetLabReportFileAsync(Guid reportId, Guid tenantId, string storageKey, string fileName,
        long sizeBytes, string mimeType, Guid? uploadedByUserId, DateTime nowUtc, CancellationToken ct);
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
    /// <summary>Marks an ABDM record LINKED (published to the national network): single-winner conditional flip
    /// (is_linked_to_phr=true, linked_at, care_context_id) on an as-yet-unlinked record in the tenant. Returns
    /// false if it was already linked / not found (→ idempotent re-link at the handler).</summary>
    Task<bool> MarkAbdmRecordLinkedAsync(Guid recordId, Guid tenantId, string? careContextId, DateTime nowUtc, CancellationToken ct);

    // ---- Drug-safety alerts (generated at prescription issue/amend) -------------------------------

    /// <summary>
    /// Active allergy + current-medication history records for safety screening (record_type IN
    /// ('allergy','medication')), with their CIPHERTEXT title/description for in-process decryption by the
    /// screening service. Enlists the ambient command tx so app.tenant_id is in scope for RLS (it is called
    /// from inside the issue/amend command's unit of work).
    /// </summary>
    Task<IReadOnlyList<MedicalHistory>> ListSafetyHistoryAsync(Guid tenantId, Guid patientId, CancellationToken ct);

    /// <summary>Bulk-inserts generated drug alerts inside the command tx (RLS admits them via the parent
    /// prescription's tenant — the prescription must already be persisted in the same tx). Returns the count written.</summary>
    Task<int> AddDrugAlertsAsync(IReadOnlyList<DrugAlert> alerts, CancellationToken ct);

    /// <summary>Lists a prescription's drug alerts (tenant-scoped via the prescription join + RLS). Read path.</summary>
    Task<IReadOnlyList<DrugAlert>> ListDrugAlertsAsync(Guid prescriptionId, Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Clinical-access context for a patient in a tenant: masked phone + general-clinical consent state
    /// (patients.consent_given_at) + ABDM consent state/expiry. No PHI beyond the masked phone.
    /// </summary>
    Task<ConsentContextRow?> GetConsentContextAsync(Guid tenantId, Guid patientId, CancellationToken ct);
}

/// <summary>Consent-context projection (masked at the infra seam). Drives the clinical-tab gating UI.</summary>
public sealed record ConsentContextRow(
    Guid PatientId, string MaskedPhone, bool ClinicalConsentActive, bool AbdmConsentActive, DateTime? AbdmConsentExpiresAt);

/// <summary>Lightweight projection of a prescription header (ciphertext fields decrypted by the handler).
/// DoctorName is plaintext directory data joined from docslot.doctors (NOT PHI).</summary>
public sealed record PrescriptionRow(Guid PrescriptionId, string? PrescriptionNumber, Guid PatientId, Guid DoctorId, string? DoctorName, string Status, DateTime CreatedAt);

/// <summary>Prescription detail-read projection: the domain entity PLUS the joined doctor name (plaintext
/// directory data from docslot.doctors — NOT PHI, not encrypted). DoctorName is read-only presentation and is
/// deliberately kept off the domain entity.</summary>
public sealed record PrescriptionDetail(Prescription Prescription, string? DoctorName);

/// <summary>Lab-report detail-read projection: the domain entity PLUS the joined test name (plaintext catalog
/// data from docslot.test_catalog — NOT PHI). TestName is read-only presentation, kept off the domain entity.</summary>
public sealed record LabReportDetail(LabReport Report, string? TestName);

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
