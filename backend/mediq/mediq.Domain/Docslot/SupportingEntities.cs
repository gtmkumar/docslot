namespace mediq.Domain.Docslot;

/// <summary>Healthcare provider (maps to <c>docslot.doctors</c>). Booking-core subset.</summary>
public sealed class Doctor
{
    public Guid DoctorId { get; private set; }
    public Guid TenantId { get; private set; }
    public string FullName { get; private set; } = default!;
    public string? DisplayName { get; private set; }
    public Guid? DepartmentId { get; private set; }
    public string? Specialization { get; private set; }
    public decimal? ConsultationFee { get; private set; }
    public decimal? FollowUpFee { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsAcceptingNewPatients { get; private set; }
    public DateTime? DeletedAt { get; private set; }

    private Doctor() { }
}

/// <summary>Hospital department (maps to <c>docslot.departments</c>).</summary>
public sealed class Department
{
    public Guid DepartmentId { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string? Code { get; private set; }
    public bool IsActive { get; private set; }

    private Department() { }
}

/// <summary>
/// Cross-tenant patient identity by phone (maps to <c>docslot.patients</c>). One phone = one patient;
/// tenant linkage lives in <c>docslot.patient_tenant_links</c>. Booking-core subset (NO clinical PHI).
/// </summary>
public sealed class Patient
{
    public Guid PatientId { get; private set; }
    public string PhoneNumber { get; private set; } = default!;
    public string? FullName { get; private set; }
    public DateOnly? DateOfBirth { get; private set; }
    public short? Age { get; private set; }
    public string? Gender { get; private set; }
    public string? Email { get; private set; }
    public string PreferredLanguage { get; private set; } = "en";
    public DateTime? ConsentGivenAt { get; private set; }
    public string? ConsentVersion { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? DeletedAt { get; private set; }

    private Patient() { }

    /// <summary>True when the patient has an active, non-withdrawn DPDP consent on file.</summary>
    public bool HasActiveConsent => ConsentGivenAt is not null && DeletedAt is null && IsActive;
}

/// <summary>Patient↔tenant relationship (maps to <c>docslot.patient_tenant_links</c>).</summary>
public sealed class PatientTenantLink
{
    public Guid LinkId { get; private set; }
    public Guid PatientId { get; private set; }
    public Guid TenantId { get; private set; }
    public string? PatientLocalId { get; private set; }
    public DateTime FirstVisitAt { get; private set; }
    public DateTime LastVisitAt { get; private set; }
    public int TotalVisits { get; private set; }

    private PatientTenantLink() { }
}

/// <summary>
/// A bookable time slot (maps to <c>docslot.time_slots</c>). Holds and bookings increment
/// <see cref="CurrentCount"/> up to <see cref="MaxCount"/>; status flips to 'booked' when full.
/// </summary>
public sealed class TimeSlot
{
    public Guid SlotId { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid DoctorId { get; private set; }
    public DateOnly SlotDate { get; private set; }
    public TimeOnly StartTime { get; private set; }
    public TimeOnly EndTime { get; private set; }
    public string Status { get; private set; } = "available";   // available | booked | blocked
    public short CurrentCount { get; private set; }
    public short MaxCount { get; private set; }

    private TimeSlot() { }

    public bool HasCapacity => Status == "available" && CurrentCount < MaxCount;
}
