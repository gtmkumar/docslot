namespace mediq.Domain.Docslot;

/// <summary>
/// The core booking aggregate (maps to <c>docslot.bookings</c>). Owns the lifecycle state machine and
/// the *_at timestamp transitions. <c>booking_number</c> (BKG-YYYY-MM-NNNNN) is assigned by the DB
/// trigger <c>trg_booking_number</c> on insert — never generated here. Status-change history is written
/// by the DB trigger <c>trg_booking_status_log</c> on UPDATE — the domain does NOT also insert history.
/// <para>
/// Valid transitions (enforced by <see cref="EnsureCanTransition"/>):
///   pending → confirmed | cancelled | no_show | rescheduled
///   confirmed → checked_in | completed | cancelled | no_show | rescheduled
///   checked_in → completed | no_show | cancelled
///   completed | cancelled | no_show | rescheduled → (terminal — no further transitions)
/// </para>
/// <para>
/// Behalf bookings (<c>booked_by_type = 'behalf'</c>) carry a claimed <c>BehalfRelation</c> and a
/// <c>PatientConsentStatus</c> that must reach <c>confirmed</c> (via the patient's WhatsApp OTP reply)
/// before the booking may be approved — the DPDP fake-patient guard, enforced in the Application layer.
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
    public string BookedFor { get; private set; } = "self";       // legacy column ('self'|'family_member'|'other')
    public string? ChiefComplaint { get; private set; }
    public string? Notes { get; private set; }
    public string? CancellationReason { get; private set; }

    // Behalf-booking identity + DPDP patient consent (09_chat_identity.sql).
    public string BookedByType { get; private set; } = Docslot.BookedByType.Self;   // 'self' | 'behalf'
    public string? BehalfRelation { get; private set; }
    public string? BehalfBookerPhone { get; private set; }
    public string PatientConsentStatus { get; private set; } = Docslot.PatientConsentStatus.NotRequired;
    public DateTime? PatientConsentAt { get; private set; }

    // Reschedule lineage: when this row is superseded, RescheduledAt is set and a NEW booking on the new
    // slot carries RescheduledFromBookingId = this.BookingId.
    public Guid? RescheduledFromBookingId { get; private set; }

    public DateTime BookedAt { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public DateTime? CheckedInAt { get; private set; }
    public DateTime? CancelledAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? NoShowAt { get; private set; }
    public DateTime? RescheduledAt { get; private set; }

    public Guid? CreatedByUserId { get; private set; }
    public Guid? CancelledByUserId { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>
    /// True when this is a behalf booking whose patient has not yet confirmed consent — approval must be
    /// blocked (DPDP fake-patient guard). Self bookings are always false.
    /// </summary>
    public bool AwaitingPatientConsent =>
        BookedByType == Docslot.BookedByType.Behalf
        && PatientConsentStatus != Docslot.PatientConsentStatus.Confirmed;

    private Booking() { }

    /// <summary>
    /// Factory for a created booking. Inserts in 'pending'. The booking_number is left null for the DB
    /// trigger to assign. Snapshot fields capture patient name/phone/age at booking time. For a behalf
    /// booking pass <paramref name="bookedByType"/>='behalf' + a valid <paramref name="behalfRelation"/>;
    /// consent defaults to 'pending' for behalf (awaiting the patient OTP) and 'not_required' for self,
    /// unless an explicit <paramref name="patientConsentStatus"/> is supplied (e.g. a reschedule carrying a
    /// previously-confirmed consent forward).
    /// </summary>
    public static Booking Create(
        Guid tenantId, Guid slotId, Guid patientId, Guid doctorId, Guid? departmentId,
        string bookingType, string bookedVia, string? patientName, string? patientPhone, short? patientAge,
        string? chiefComplaint, string? notes, Guid? createdByUserId, DateTime nowUtc,
        string bookedByType = Docslot.BookedByType.Self, string? behalfRelation = null,
        string? behalfBookerPhone = null, string? patientConsentStatus = null,
        DateTime? patientConsentAt = null, Guid? rescheduledFromBookingId = null)
    {
        var isBehalf = bookedByType == Docslot.BookedByType.Behalf;
        if (isBehalf && !Docslot.BehalfRelation.IsValid(behalfRelation))
            throw new ArgumentException("A valid behalf_relation is required for a behalf booking.", nameof(behalfRelation));

        var consent = patientConsentStatus
            ?? (isBehalf ? Docslot.PatientConsentStatus.Pending : Docslot.PatientConsentStatus.NotRequired);

        return new()
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
            BookedFor = isBehalf ? "other" : "self",
            BookedByType = bookedByType,
            BehalfRelation = isBehalf ? behalfRelation : null,
            BehalfBookerPhone = isBehalf ? behalfBookerPhone : null,
            PatientConsentStatus = consent,
            PatientConsentAt = patientConsentAt,
            RescheduledFromBookingId = rescheduledFromBookingId,
            PatientNameAtBooking = patientName,
            PatientPhoneAtBooking = patientPhone,
            PatientAgeAtBooking = patientAge,
            ChiefComplaint = chiefComplaint,
            Notes = notes,
            CreatedByUserId = createdByUserId,
            BookedAt = nowUtc,
            UpdatedAt = nowUtc,
        };
    }

    // ---- State machine -----------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<BookingStatus, BookingStatus[]> Allowed =
        new Dictionary<BookingStatus, BookingStatus[]>
        {
            [BookingStatus.Pending] = [BookingStatus.Confirmed, BookingStatus.Cancelled, BookingStatus.NoShow, BookingStatus.Rescheduled],
            [BookingStatus.Confirmed] = [BookingStatus.CheckedIn, BookingStatus.Completed, BookingStatus.Cancelled, BookingStatus.NoShow, BookingStatus.Rescheduled],
            [BookingStatus.CheckedIn] = [BookingStatus.Completed, BookingStatus.NoShow, BookingStatus.Cancelled],
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

    /// <summary>Front-desk arrival: confirmed → checked_in. Records the arrival time.</summary>
    public void CheckIn(DateTime nowUtc)
    {
        EnsureCanTransition(BookingStatus.CheckedIn, "check in");
        _status = BookingStatusTokens.CheckedIn;
        CheckedInAt = nowUtc;
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
    /// by the Application handler (carrying <c>RescheduledFromBookingId = this.BookingId</c>); this just
    /// terminates the old one. Sets cancelled_by for attribution and records rescheduled_at.
    /// </summary>
    public void MarkRescheduled(Guid? byUserId, DateTime nowUtc)
    {
        EnsureCanTransition(BookingStatus.Rescheduled, "reschedule");
        _status = BookingStatusTokens.Rescheduled;
        CancelledByUserId = byUserId;
        RescheduledAt = nowUtc;
        UpdatedAt = nowUtc;
    }

    /// <summary>
    /// Records the patient's WhatsApp OTP consent on a behalf booking (pending → confirmed). No-op-safe to
    /// call only when currently behalf+pending; throws otherwise so a stray confirm can't mutate a self or
    /// already-decided booking.
    /// </summary>
    public void ConfirmPatientConsent(DateTime nowUtc)
    {
        if (BookedByType != Docslot.BookedByType.Behalf)
            throw new InvalidOperationException("Patient consent applies only to behalf bookings.");
        if (PatientConsentStatus != Docslot.PatientConsentStatus.Pending)
            throw new InvalidOperationException($"Consent is '{PatientConsentStatus}', not pending; cannot confirm.");
        PatientConsentStatus = Docslot.PatientConsentStatus.Confirmed;
        PatientConsentAt = nowUtc;
        UpdatedAt = nowUtc;
    }

    /// <summary>
    /// Records that the patient declined consent on a behalf booking (pending → denied). The caller is
    /// responsible for cancelling the booking + freeing the slot.
    /// </summary>
    public void DenyPatientConsent(DateTime nowUtc)
    {
        if (BookedByType != Docslot.BookedByType.Behalf)
            throw new InvalidOperationException("Patient consent applies only to behalf bookings.");
        if (PatientConsentStatus != Docslot.PatientConsentStatus.Pending)
            throw new InvalidOperationException($"Consent is '{PatientConsentStatus}', not pending; cannot deny.");
        PatientConsentStatus = Docslot.PatientConsentStatus.Denied;
        UpdatedAt = nowUtc;
    }

    /// <summary>
    /// Records that the patient's consent code lapsed before approval (pending → expired). The caller is
    /// responsible for cancelling the booking + freeing the slot. (The maintenance worker's sweep does this
    /// in bulk via a DB function; this is the in-band path when a late reply arrives first.)
    /// </summary>
    public void ExpirePatientConsent(DateTime nowUtc)
    {
        if (BookedByType != Docslot.BookedByType.Behalf)
            throw new InvalidOperationException("Patient consent applies only to behalf bookings.");
        if (PatientConsentStatus != Docslot.PatientConsentStatus.Pending)
            throw new InvalidOperationException($"Consent is '{PatientConsentStatus}', not pending; cannot expire.");
        PatientConsentStatus = Docslot.PatientConsentStatus.Expired;
        UpdatedAt = nowUtc;
    }
}
