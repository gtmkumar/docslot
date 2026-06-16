namespace mediq.Domain.Docslot;

/// <summary>
/// The core booking aggregate (maps to <c>docslot.bookings</c>). Owns the lifecycle state machine and
/// the *_at timestamp transitions. <c>booking_number</c> (BKG-YYYY-MM-NNNNN) is assigned by the DB
/// trigger <c>trg_booking_number</c> on insert — never generated here. Status-change history is written
/// by the DB trigger <c>trg_booking_status_log</c> on UPDATE — the domain does NOT also insert history.
/// <para>
/// Valid transitions (enforced by <see cref="EnsureCanTransition"/>):
///   pending → confirmed | cancelled | no_show | rescheduled
///   confirmed → completed | cancelled | no_show | rescheduled
///   completed | cancelled | no_show | rescheduled → (terminal — no further transitions)
/// </para>
/// </summary>
public sealed class Booking
{
    public Guid BookingId { get; private set; }
    public string? BookingNumber { get; private set; }     // null until the insert trigger assigns it
    public Guid TenantId { get; private set; }
    public Guid SlotId { get; private set; }
    public Guid PatientId { get; private set; }
    public Guid DoctorId { get; private set; }
    public Guid? DepartmentId { get; private set; }

    public string BookingType { get; private set; } = "consultation";
    private string _status = BookingStatusTokens.Pending;
    public BookingStatus Status => BookingStatusTokens.FromToken(_status);

    public string? PatientNameAtBooking { get; private set; }
    public string? PatientPhoneAtBooking { get; private set; }
    public short? PatientAgeAtBooking { get; private set; }

    public string BookedVia { get; private set; } = "dashboard";
    public string BookedFor { get; private set; } = "self";
    public string? ChiefComplaint { get; private set; }
    public string? Notes { get; private set; }
    public string? CancellationReason { get; private set; }

    public DateTime BookedAt { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public DateTime? CancelledAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? NoShowAt { get; private set; }

    public Guid? CreatedByUserId { get; private set; }
    public Guid? CancelledByUserId { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private Booking() { }

    /// <summary>
    /// Factory for a staff/walk-in created booking. Inserts in 'pending'. The booking_number is left null
    /// for the DB trigger to assign. Snapshot fields capture patient name/phone/age at booking time.
    /// </summary>
    public static Booking Create(
        Guid tenantId, Guid slotId, Guid patientId, Guid doctorId, Guid? departmentId,
        string bookingType, string bookedVia, string? patientName, string? patientPhone, short? patientAge,
        string? chiefComplaint, string? notes, Guid? createdByUserId, DateTime nowUtc)
        => new()
        {
            BookingId = Guid.CreateVersion7(),
            TenantId = tenantId,
            SlotId = slotId,
            PatientId = patientId,
            DoctorId = doctorId,
            DepartmentId = departmentId,
            BookingType = bookingType,
            _status = BookingStatusTokens.Pending,
            BookedVia = bookedVia,
            BookedFor = "self",
            PatientNameAtBooking = patientName,
            PatientPhoneAtBooking = patientPhone,
            PatientAgeAtBooking = patientAge,
            ChiefComplaint = chiefComplaint,
            Notes = notes,
            CreatedByUserId = createdByUserId,
            BookedAt = nowUtc,
            UpdatedAt = nowUtc,
        };

    // ---- State machine -----------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<BookingStatus, BookingStatus[]> Allowed =
        new Dictionary<BookingStatus, BookingStatus[]>
        {
            [BookingStatus.Pending] = [BookingStatus.Confirmed, BookingStatus.Cancelled, BookingStatus.NoShow, BookingStatus.Rescheduled],
            [BookingStatus.Confirmed] = [BookingStatus.Completed, BookingStatus.Cancelled, BookingStatus.NoShow, BookingStatus.Rescheduled],
            [BookingStatus.Completed] = [],
            [BookingStatus.Cancelled] = [],
            [BookingStatus.NoShow] = [],
            [BookingStatus.Rescheduled] = [],
        };

    private void EnsureCanTransition(BookingStatus to, string action)
    {
        if (!Allowed[Status].Contains(to))
            throw new InvalidBookingTransitionException(Status, action);
    }

    public void Approve(DateTime nowUtc)
    {
        EnsureCanTransition(BookingStatus.Confirmed, "approve");
        _status = BookingStatusTokens.Confirmed;
        ConfirmedAt = nowUtc;
        UpdatedAt = nowUtc;
    }

    public void Cancel(string reason, Guid? byUserId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A cancellation reason is required.", nameof(reason));
        EnsureCanTransition(BookingStatus.Cancelled, "cancel");
        _status = BookingStatusTokens.Cancelled;
        CancellationReason = reason;
        CancelledByUserId = byUserId;
        CancelledAt = nowUtc;
        UpdatedAt = nowUtc;
    }

    public void MarkNoShow(DateTime nowUtc)
    {
        EnsureCanTransition(BookingStatus.NoShow, "mark no-show");
        _status = BookingStatusTokens.NoShow;
        NoShowAt = nowUtc;
        UpdatedAt = nowUtc;
    }

    public void Complete(DateTime nowUtc)
    {
        EnsureCanTransition(BookingStatus.Completed, "complete");
        _status = BookingStatusTokens.Completed;
        CompletedAt = nowUtc;
        UpdatedAt = nowUtc;
    }

    /// <summary>
    /// Marks this booking as superseded by a reschedule. The NEW slot's booking is a separate row created
    /// by the Application handler; this just terminates the old one. Sets cancelled_by for attribution.
    /// </summary>
    public void MarkRescheduled(Guid? byUserId, DateTime nowUtc)
    {
        EnsureCanTransition(BookingStatus.Rescheduled, "reschedule");
        _status = BookingStatusTokens.Rescheduled;
        CancelledByUserId = byUserId;
        UpdatedAt = nowUtc;
    }
}
