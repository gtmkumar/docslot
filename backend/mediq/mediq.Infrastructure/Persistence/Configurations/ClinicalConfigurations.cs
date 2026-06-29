using mediq.Domain.Docslot;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace mediq.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the clinical PHI tables (slice 03b). These are RLS-protected and field-encrypted; the
/// ClinicalRepository does all I/O via parameterized raw SQL (jsonb-encrypted columns need to_jsonb/#>>
/// handling and immediate-flush for the PRX-/RPT- number triggers), so EF only needs valid mappings.
/// jsonb-encrypted columns (medications/structured_results/fhir_bundle) are mapped as string for reads.
/// </summary>
public sealed class PrescriptionConfiguration : IEntityTypeConfiguration<Prescription>
{
    public void Configure(EntityTypeBuilder<Prescription> b)
    {
        b.ToTable("prescriptions", "docslot");
        b.HasKey(x => x.PrescriptionId);
        b.Property(x => x.PrescriptionId).HasColumnName("prescription_id");
        b.Property(x => x.PrescriptionNumber).HasColumnName("prescription_number").ValueGeneratedOnAdd();
        b.Property(x => x.BookingId).HasColumnName("booking_id");
        b.Property(x => x.PatientId).HasColumnName("patient_id");
        b.Property(x => x.DoctorId).HasColumnName("doctor_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.ChiefComplaintsEnc).HasColumnName("chief_complaints");
        b.Property(x => x.ExaminationEnc).HasColumnName("examination");
        b.Property(x => x.DiagnosisEnc).HasColumnName("diagnosis");
        b.Property(x => x.MedicationsEnc).HasColumnName("medications").HasColumnType("jsonb");
        b.Property(x => x.Advice).HasColumnName("advice");
        b.Property(x => x.FollowUpInDays).HasColumnName("follow_up_in_days");
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.SupersedesPrescriptionId).HasColumnName("supersedes_prescription_id");
        b.Property(x => x.AmendmentReason).HasColumnName("amendment_reason");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}

public sealed class LabReportConfiguration : IEntityTypeConfiguration<LabReport>
{
    public void Configure(EntityTypeBuilder<LabReport> b)
    {
        b.ToTable("lab_reports", "docslot");
        b.HasKey(x => x.ReportId);
        b.Property(x => x.ReportId).HasColumnName("report_id");
        b.Property(x => x.ReportNumber).HasColumnName("report_number").ValueGeneratedOnAdd();
        b.Property(x => x.BookingId).HasColumnName("booking_id");
        b.Property(x => x.PatientId).HasColumnName("patient_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.TestId).HasColumnName("test_id");
        b.Property(x => x.FileName).HasColumnName("file_name");
        b.Property(x => x.StructuredResultsEnc).HasColumnName("structured_results").HasColumnType("jsonb");
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.HasCriticalFindings).HasColumnName("has_critical_findings");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}

public sealed class MedicalHistoryConfiguration : IEntityTypeConfiguration<MedicalHistory>
{
    public void Configure(EntityTypeBuilder<MedicalHistory> b)
    {
        b.ToTable("patient_medical_history", "docslot");
        b.HasKey(x => x.HistoryId);
        b.Property(x => x.HistoryId).HasColumnName("history_id");
        b.Property(x => x.PatientId).HasColumnName("patient_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.RecordType).HasColumnName("record_type");
        b.Property(x => x.TitleEnc).HasColumnName("title");
        b.Property(x => x.DescriptionEnc).HasColumnName("description");
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.IsCritical).HasColumnName("is_critical");
        b.Property(x => x.AddedAt).HasColumnName("added_at");
    }
}

public sealed class AbdmHealthRecordConfiguration : IEntityTypeConfiguration<AbdmHealthRecord>
{
    public void Configure(EntityTypeBuilder<AbdmHealthRecord> b)
    {
        b.ToTable("abdm_health_records", "docslot");
        b.HasKey(x => x.RecordId);
        b.Property(x => x.RecordId).HasColumnName("record_id");
        b.Property(x => x.PatientId).HasColumnName("patient_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.BookingId).HasColumnName("booking_id");
        b.Property(x => x.AbhaNumber).HasColumnName("abha_number");
        b.Property(x => x.RecordType).HasColumnName("record_type");
        b.Property(x => x.FhirBundleEnc).HasColumnName("fhir_bundle").HasColumnType("jsonb");
        b.Property(x => x.IsLinkedToPhr).HasColumnName("is_linked_to_phr");
        b.Property(x => x.ConsentId).HasColumnName("consent_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}
