using mediq.Domain.Docslot;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace mediq.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the docslot booking-core tables (database/03_docslot.sql) to schema <c>docslot</c>. Database-first;
/// only booking-core columns are mapped (clinical PHI tables deferred to 03b/05). DATE→DateOnly,
/// TIME→TimeOnly via Npgsql. The Booking <c>_status</c> backing field is mapped to the string column.
/// </summary>
public sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> b)
    {
        b.ToTable("bookings", "docslot");
        b.HasKey(x => x.BookingId);
        b.Property(x => x.BookingId).HasColumnName("booking_id");
        b.Property(x => x.BookingNumber).HasColumnName("booking_number").ValueGeneratedOnAdd();
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.SlotId).HasColumnName("slot_id");
        b.Property(x => x.PatientId).HasColumnName("patient_id");
        b.Property(x => x.DoctorId).HasColumnName("doctor_id");
        b.Property(x => x.DepartmentId).HasColumnName("department_id");
        b.Property(x => x.BookingType).HasColumnName("booking_type");
        b.Property<string>("_status").HasColumnName("status").HasField("_status").UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Ignore(x => x.Status);   // computed wrapper over _status
        b.Property(x => x.PatientNameAtBooking).HasColumnName("patient_name_at_booking");
        b.Property(x => x.PatientPhoneAtBooking).HasColumnName("patient_phone_at_booking");
        b.Property(x => x.PatientAgeAtBooking).HasColumnName("patient_age_at_booking");
        b.Property(x => x.BookedVia).HasColumnName("booked_via");
        b.Property(x => x.BookedFor).HasColumnName("booked_for");
        b.Property(x => x.BookedByType).HasColumnName("booked_by_type");
        b.Property(x => x.BehalfRelation).HasColumnName("behalf_relation");
        b.Property(x => x.BehalfBookerPhone).HasColumnName("behalf_booker_phone");
        b.Property(x => x.PatientConsentStatus).HasColumnName("patient_consent_status");
        b.Property(x => x.PatientConsentAt).HasColumnName("patient_consent_at");
        b.Property(x => x.RescheduledFromBookingId).HasColumnName("rescheduled_from_booking_id");
        b.Ignore(x => x.AwaitingPatientConsent);   // computed over consent fields
        b.Property(x => x.ChiefComplaint).HasColumnName("chief_complaint");
        b.Property(x => x.Notes).HasColumnName("notes");
        b.Property(x => x.CancellationReason).HasColumnName("cancellation_reason");
        b.Property(x => x.BookedAt).HasColumnName("booked_at");
        b.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at");
        b.Property(x => x.CheckedInAt).HasColumnName("checked_in_at");
        b.Property(x => x.CancelledAt).HasColumnName("cancelled_at");
        b.Property(x => x.CompletedAt).HasColumnName("completed_at");
        b.Property(x => x.NoShowAt).HasColumnName("no_show_at");
        b.Property(x => x.RescheduledAt).HasColumnName("rescheduled_at");
        b.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        b.Property(x => x.CancelledByUserId).HasColumnName("cancelled_by_user_id");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}

public sealed class DoctorConfiguration : IEntityTypeConfiguration<Doctor>
{
    public void Configure(EntityTypeBuilder<Doctor> b)
    {
        b.ToTable("doctors", "docslot");
        b.HasKey(x => x.DoctorId);
        b.Property(x => x.DoctorId).HasColumnName("doctor_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.FullName).HasColumnName("full_name");
        b.Property(x => x.DisplayName).HasColumnName("display_name");
        b.Property(x => x.DepartmentId).HasColumnName("department_id");
        b.Property(x => x.Specialization).HasColumnName("specialization");
        b.Property(x => x.ConsultationFee).HasColumnName("consultation_fee");
        b.Property(x => x.FollowUpFee).HasColumnName("follow_up_fee");
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.IsAcceptingNewPatients).HasColumnName("is_accepting_new_patients");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
    }
}

public sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> b)
    {
        b.ToTable("departments", "docslot");
        b.HasKey(x => x.DepartmentId);
        b.Property(x => x.DepartmentId).HasColumnName("department_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Code).HasColumnName("code");
        b.Property(x => x.IsActive).HasColumnName("is_active");
    }
}

public sealed class PatientConfiguration : IEntityTypeConfiguration<Patient>
{
    public void Configure(EntityTypeBuilder<Patient> b)
    {
        b.ToTable("patients", "docslot");
        b.HasKey(x => x.PatientId);
        b.Property(x => x.PatientId).HasColumnName("patient_id");
        b.Property(x => x.PhoneNumber).HasColumnName("phone_number");
        b.Property(x => x.FullName).HasColumnName("full_name");
        b.Property(x => x.DateOfBirth).HasColumnName("date_of_birth");
        b.Property(x => x.Age).HasColumnName("age");
        b.Property(x => x.Gender).HasColumnName("gender");
        b.Property(x => x.Email).HasColumnName("email").HasColumnType("citext");
        b.Property(x => x.PreferredLanguage).HasColumnName("preferred_language");
        b.Property(x => x.ConsentGivenAt).HasColumnName("consent_given_at");
        b.Property(x => x.ConsentVersion).HasColumnName("consent_version");
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
    }
}

public sealed class PatientTenantLinkConfiguration : IEntityTypeConfiguration<PatientTenantLink>
{
    public void Configure(EntityTypeBuilder<PatientTenantLink> b)
    {
        b.ToTable("patient_tenant_links", "docslot");
        b.HasKey(x => x.LinkId);
        b.Property(x => x.LinkId).HasColumnName("link_id");
        b.Property(x => x.PatientId).HasColumnName("patient_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.PatientLocalId).HasColumnName("patient_local_id");
        b.Property(x => x.FirstVisitAt).HasColumnName("first_visit_at");
        b.Property(x => x.LastVisitAt).HasColumnName("last_visit_at");
        b.Property(x => x.TotalVisits).HasColumnName("total_visits");
    }
}

public sealed class TimeSlotConfiguration : IEntityTypeConfiguration<TimeSlot>
{
    public void Configure(EntityTypeBuilder<TimeSlot> b)
    {
        b.ToTable("time_slots", "docslot");
        b.HasKey(x => x.SlotId);
        b.Property(x => x.SlotId).HasColumnName("slot_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.DoctorId).HasColumnName("doctor_id");
        b.Property(x => x.SlotDate).HasColumnName("slot_date");
        b.Property(x => x.StartTime).HasColumnName("start_time");
        b.Property(x => x.EndTime).HasColumnName("end_time");
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.CurrentCount).HasColumnName("current_count");
        b.Property(x => x.MaxCount).HasColumnName("max_count");
    }
}
