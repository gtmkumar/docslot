namespace mediq.Domain.Docslot;

/// <summary>
/// Prescription (maps to <c>docslot.prescriptions</c>) — CLINICAL PHI. Several fields are encrypted at rest
/// via the field-encryption service (diagnosis/medications/examination/chief_complaints): the entity holds
/// the CIPHERTEXT envelope strings; the Application decrypts on authorized read. prescription_number
/// (PRX-...) is assigned by the DB trigger. RLS-protected by tenant_id.
/// </summary>
public sealed class Prescription
{
    public Guid PrescriptionId { get; private set; }
    public string? PrescriptionNumber { get; private set; }   // trigger-assigned
    public Guid BookingId { get; private set; }
    public Guid PatientId { get; private set; }
    public Guid DoctorId { get; private set; }
    public Guid TenantId { get; private set; }

    // Encrypted-at-rest envelope strings (NOT plaintext).
    public string? ChiefComplaintsEnc { get; private set; }
    public string? ExaminationEnc { get; private set; }
    public string? DiagnosisEnc { get; private set; }
    public string MedicationsEnc { get; private set; } = default!;   // NOT NULL jsonb in schema → store envelope text

    public string? Advice { get; private set; }
    public int? FollowUpInDays { get; private set; }
    public string Status { get; private set; } = "draft";
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private Prescription() { }

    public static Prescription Issue(
        Guid bookingId, Guid patientId, Guid doctorId, Guid tenantId,
        string? chiefComplaintsEnc, string? examinationEnc, string? diagnosisEnc, string medicationsEnc,
        string? advice, int? followUpInDays, DateTime nowUtc)
        => new()
        {
            PrescriptionId = Guid.CreateVersion7(),
            BookingId = bookingId, PatientId = patientId, DoctorId = doctorId, TenantId = tenantId,
            ChiefComplaintsEnc = chiefComplaintsEnc, ExaminationEnc = examinationEnc,
            DiagnosisEnc = diagnosisEnc, MedicationsEnc = medicationsEnc,
            Advice = advice, FollowUpInDays = followUpInDays,
            Status = "finalized", CreatedAt = nowUtc, UpdatedAt = nowUtc,
        };

    /// <summary>Rehydrates from a persisted row (read path) — identity + trigger-assigned number preserved.</summary>
    public static Prescription FromRow(
        Guid id, string? number, Guid bookingId, Guid patientId, Guid doctorId, Guid tenantId,
        string? chiefEnc, string? examEnc, string? diagEnc, string medsEnc, string? advice, int? followUp, string status, DateTime createdAt)
        => new()
        {
            PrescriptionId = id, PrescriptionNumber = number, BookingId = bookingId, PatientId = patientId,
            DoctorId = doctorId, TenantId = tenantId, ChiefComplaintsEnc = chiefEnc, ExaminationEnc = examEnc,
            DiagnosisEnc = diagEnc, MedicationsEnc = medsEnc, Advice = advice, FollowUpInDays = followUp,
            Status = status, CreatedAt = createdAt, UpdatedAt = createdAt,
        };
}

/// <summary>Lab report (maps to <c>docslot.lab_reports</c>) — PHI. structured_results encrypted at rest. RLS by tenant_id.</summary>
public sealed class LabReport
{
    public Guid ReportId { get; private set; }
    public string? ReportNumber { get; private set; }   // trigger-assigned RPT-...
    public Guid BookingId { get; private set; }
    public Guid PatientId { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid? TestId { get; private set; }
    public string? FileName { get; private set; }
    public string? StructuredResultsEnc { get; private set; }   // encrypted envelope
    public string Status { get; private set; } = "pending";
    public bool HasCriticalFindings { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private LabReport() { }

    public static LabReport Upload(
        Guid bookingId, Guid patientId, Guid tenantId, Guid? testId, string? fileName,
        string? structuredResultsEnc, bool hasCriticalFindings, DateTime nowUtc)
        => new()
        {
            ReportId = Guid.CreateVersion7(),
            BookingId = bookingId, PatientId = patientId, TenantId = tenantId, TestId = testId,
            FileName = fileName, StructuredResultsEnc = structuredResultsEnc,
            HasCriticalFindings = hasCriticalFindings, Status = "ready",
            CreatedAt = nowUtc, UpdatedAt = nowUtc,
        };

    public static LabReport FromRow(
        Guid id, string? number, Guid bookingId, Guid patientId, Guid tenantId, Guid? testId, string? fileName,
        string? resultsEnc, string status, bool hasCritical, DateTime createdAt)
        => new()
        {
            ReportId = id, ReportNumber = number, BookingId = bookingId, PatientId = patientId, TenantId = tenantId,
            TestId = testId, FileName = fileName, StructuredResultsEnc = resultsEnc, Status = status,
            HasCriticalFindings = hasCritical, CreatedAt = createdAt, UpdatedAt = createdAt,
        };
}

/// <summary>Patient medical history (maps to <c>docslot.patient_medical_history</c>) — PHI. title/description encrypted. RLS by tenant_id.</summary>
public sealed class MedicalHistory
{
    public Guid HistoryId { get; private set; }
    public Guid PatientId { get; private set; }
    public Guid TenantId { get; private set; }
    public string RecordType { get; private set; } = default!;
    public string TitleEnc { get; private set; } = default!;        // NOT NULL → encrypted envelope
    public string? DescriptionEnc { get; private set; }             // encrypted envelope
    public bool IsActive { get; private set; }
    public bool IsCritical { get; private set; }
    public DateTime AddedAt { get; private set; }

    private MedicalHistory() { }

    public static MedicalHistory FromRow(
        Guid id, Guid patientId, Guid tenantId, string recordType, string titleEnc, string? descEnc,
        bool isActive, bool isCritical, DateTime addedAt)
        => new()
        {
            HistoryId = id, PatientId = patientId, TenantId = tenantId, RecordType = recordType,
            TitleEnc = titleEnc, DescriptionEnc = descEnc, IsActive = isActive, IsCritical = isCritical, AddedAt = addedAt,
        };
}

/// <summary>ABDM FHIR health record (maps to <c>docslot.abdm_health_records</c>) — PHI. fhir_bundle encrypted; consent-gated. RLS by tenant_id.</summary>
public sealed class AbdmHealthRecord
{
    public Guid RecordId { get; private set; }
    public Guid PatientId { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid? BookingId { get; private set; }
    public string AbhaNumber { get; private set; } = default!;
    public string RecordType { get; private set; } = default!;
    public string FhirBundleEnc { get; private set; } = default!;   // NOT NULL jsonb → encrypted envelope text
    public bool IsLinkedToPhr { get; private set; }
    public string? ConsentId { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private AbdmHealthRecord() { }

    public static AbdmHealthRecord Push(
        Guid patientId, Guid tenantId, Guid? bookingId, string abhaNumber, string recordType,
        string fhirBundleEnc, string? consentId, DateTime nowUtc)
        => new()
        {
            RecordId = Guid.CreateVersion7(),
            PatientId = patientId, TenantId = tenantId, BookingId = bookingId,
            AbhaNumber = abhaNumber, RecordType = recordType, FhirBundleEnc = fhirBundleEnc,
            ConsentId = consentId, IsLinkedToPhr = false, CreatedAt = nowUtc,
        };

    public static AbdmHealthRecord FromRow(
        Guid id, Guid patientId, Guid tenantId, Guid? bookingId, string abhaNumber, string recordType,
        string fhirBundleEnc, bool isLinkedToPhr, string? consentId, DateTime createdAt)
        => new()
        {
            RecordId = id, PatientId = patientId, TenantId = tenantId, BookingId = bookingId,
            AbhaNumber = abhaNumber, RecordType = recordType, FhirBundleEnc = fhirBundleEnc,
            IsLinkedToPhr = isLinkedToPhr, ConsentId = consentId, CreatedAt = createdAt,
        };
}
