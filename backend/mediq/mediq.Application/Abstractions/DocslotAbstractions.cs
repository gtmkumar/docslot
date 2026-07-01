using mediq.Application.Features.Docslot.Settings;
using mediq.Domain.Docslot;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;
using mediq.SharedDataModel.Docslot.Doctors;

namespace mediq.Application.Abstractions;

/// <summary>
/// Tenant Settings reads (<c>docslot.healthcare_facilities</c>, one row per tenant). Projects the operational
/// config + WhatsApp/HFR connection status. The <c>whatsapp_access_token</c> secret is NEVER selected.
/// </summary>
public interface ISettingsReadService
{
    /// <summary>The tenant's facility settings, or null when no facility row exists (→ 404 upstream).</summary>
    Task<SettingsDto?> GetAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>
/// Write-side facility settings. The PATCH only mutates the two editable jsonb columns
/// (<c>business_hours</c>, <c>appointment_settings</c>) + <c>updated_at</c>, tenant-scoped, inside the command's
/// UnitOfWork transaction (RLS-honoured). A thin guarded UPDATE against an existing schema — earns its place
/// purely to keep raw SQL out of the Application handler; no Repository aggregate is warranted.
/// </summary>
public interface ISettingsRepository
{
    /// <summary>
    /// Updates the supplied sections (a null section is left untouched). Returns false when no facility row
    /// exists for the tenant (nothing updated) → the handler maps that to a 404.
    /// </summary>
    Task<bool> UpdateSettingsAsync(
        Guid tenantId,
        IReadOnlyDictionary<string, DayHours>? businessHours,
        AppointmentSettingsDto? appointmentSettings,
        DateTime nowUtc,
        CancellationToken ct);
}

/// <summary>
/// Write-side access to <c>docslot.bookings</c> — the booking aggregate. Earns the Repository pattern
/// (lifecycle invariants + snapshot loading). Read-side list/summary queries project off the DbContext.
/// </summary>
public interface IBookingRepository
{
    Task<Booking?> GetByIdAsync(Guid bookingId, Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Inserts the booking and FLUSHES immediately (the DB trigger assigns booking_number on insert, and
    /// the OPD token / hold-conversion that follow reference the row by FK). Returns the trigger-assigned
    /// booking_number.
    /// </summary>
    Task<string?> AddAndSaveAsync(Booking booking, CancellationToken ct);

    /// <summary>Re-reads the booking_number the DB trigger assigned after insert (for the action result / events).</summary>
    Task<string?> GetBookingNumberAsync(Guid bookingId, CancellationToken ct);
}

/// <summary>Booking read-models (Dashboard contract) — projected directly, tenant-scoped, masked phone.</summary>
public interface IBookingReadService
{
    Task<DashboardSummaryDto> GetSummaryAsync(Guid tenantId, CancellationToken ct);
    Task<(IReadOnlyList<BookingListItemDto> Items, int Total)> ListAsync(BookingListFilter filter, CancellationToken ct);
    Task<BookingListItemDto?> GetItemAsync(Guid tenantId, Guid bookingId, CancellationToken ct);
    Task<IReadOnlyList<ConversationMessageDto>> GetConversationAsync(Guid tenantId, Guid bookingId, CancellationToken ct);
    /// <summary>Non-PHI no-show feature snapshot for a booking (lead time, slot hour, on-behalf) — feeds the
    /// AI no-show client / stub heuristic. Null if the booking is not in the tenant.</summary>
    Task<NoShowFeatures?> GetNoShowFeaturesAsync(Guid tenantId, Guid bookingId, CancellationToken ct);
}

public sealed record BookingListFilter(
    Guid TenantId, string? Status, DateOnly? Date, Guid? DoctorId, int Skip, int Take);

/// <summary>A WhatsApp/booking conversation message (maps to <c>docslot.wa_message_log</c>) for the panel.</summary>
public sealed record ConversationMessageDto(
    Guid LogId, string Direction, string MessageType, string? Content, string? Status, DateTimeOffset SentAt);

/// <summary>Doctor + slot reads for the booking surface.</summary>
public interface IDoctorReadService
{
    Task<IReadOnlyList<DoctorDto>> ListAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<SlotDto>> GetSlotsAsync(Guid tenantId, Guid doctorId, DateOnly date, CancellationToken ct);

    /// <summary>True if the (active, non-deleted) doctor belongs to the tenant — the cross-tenant guard for
    /// doctor-scoped writes (slot generation, schedule edits) so a tenant can't act on another's doctor.</summary>
    Task<bool> ExistsInTenantAsync(Guid doctorId, Guid tenantId, CancellationToken ct);

    /// <summary>
    /// The doctor's recurring weekly schedule blocks (<c>docslot.doctor_schedules</c>), ordered by weekday then
    /// start time. The cross-tenant guard is the CALLER'S responsibility (handlers gate via
    /// <see cref="ExistsInTenantAsync"/> first); this projection itself joins on doctor_id only.
    /// </summary>
    Task<IReadOnlyList<ScheduleBlockDto>> GetSchedulesAsync(Guid doctorId, CancellationToken ct);

    /// <summary>
    /// The doctor's date-specific schedule overrides (<c>docslot.schedule_overrides</c>), optionally from
    /// <paramref name="from"/> (inclusive) onward, ordered by date. Cross-tenant guard is the caller's job.
    /// </summary>
    Task<IReadOnlyList<ScheduleOverrideDto>> GetOverridesAsync(Guid doctorId, DateOnly? from, CancellationToken ct);
}

/// <summary>
/// Doctor directory card. The trailing fields are ADDITIVE + NULLABLE (slice — directory enrichment):
/// existing consumers that ignore them are unaffected, and any missing source data projects as null
/// (never throws). None of these are PHI — doctor data + aggregate slot counts + average rating only.
/// </summary>
public sealed record DoctorDto(
    Guid DoctorId, string FullName, string? DisplayName, string? Specialization,
    Guid? DepartmentId, decimal? ConsultationFee, bool IsAcceptingNewPatients,
    string? DepartmentName = null,
    short? TodayBooked = null,
    short? TodayCapacity = null,
    decimal? Rating = null,
    TimeOnly? TodayHoursStart = null,
    TimeOnly? TodayHoursEnd = null,
    DateTimeOffset? NextAvailableSlot = null);

public sealed record SlotDto(
    Guid SlotId, Guid DoctorId, DateOnly SlotDate, TimeOnly StartTime, TimeOnly EndTime,
    string Status, short CurrentCount, short MaxCount);

/// <summary>
/// Tenant-scoped analytics aggregates (Analytics screen). NO PHI — every result is a tenant-level
/// aggregate. The period bounds the booked_at / slot_date range in Asia/Kolkata.
/// </summary>
public interface IAnalyticsReadService
{
    Task<AnalyticsDto> GetAnalyticsAsync(Guid tenantId, AnalyticsPeriod period, CancellationToken ct);
}

/// <summary>Analytics aggregation window. Bounds are computed in Asia/Kolkata against CURRENT_DATE.</summary>
public enum AnalyticsPeriod { Month, Quarter, Year }

/// <summary>
/// Navigation badge counts keyed by <c>platform.navigation_menus.badge_source</c>. Counts are tenant-scoped
/// aggregates (no PHI). Unknown/uncomputable sources project to 0 rather than being omitted.
/// </summary>
public interface IBadgeReadService
{
    Task<IReadOnlyDictionary<string, int>> GetBadgeCountsAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>Patient reads (masked list) + tenant linkage. Full-record read is purpose-of-use-gated.</summary>
public interface IPatientReadService
{
    Task<IReadOnlyList<PatientListItemDto>> ListAsync(Guid tenantId, int skip, int take, CancellationToken ct);

    /// <summary>
    /// Patient booking-core detail. <paramref name="maskPhone"/> drives the receptionist sensitive-field masking
    /// (issue #91): when true the phone is partially masked; when false the full number is returned. The caller
    /// (the purpose-gated handler) decides based on the tenant policy + the caller's permissions — clinical staff
    /// are exempt from masking, front-desk staff are masked when the tenant enables it.
    /// </summary>
    Task<PatientDetailDto?> GetDetailAsync(Guid tenantId, Guid patientId, bool maskPhone, CancellationToken ct);
}

/// <summary>Patient list row — MASKED phone only (PHI). Raw phone never serialized.</summary>
public sealed record PatientListItemDto(
    Guid PatientId, string? FullName, string MaskedPhone, short? Age, string? Gender, string PreferredLanguage);

/// <summary>Patient detail — booking-core demographics only; NO clinical PHI (prescriptions/labs deferred to 03b/05).</summary>
public sealed record PatientDetailDto(
    Guid PatientId, string? FullName, string MaskedPhone, DateOnly? DateOfBirth, short? Age,
    string? Gender, string? Email, string PreferredLanguage, bool HasActiveConsent);

/// <summary>Write-side patient provisioning + tenant linkage.</summary>
public interface IPatientRepository
{
    Task<Patient?> GetByPhoneAsync(string phoneNumber, CancellationToken ct);
    Task<Patient?> GetByIdAsync(Guid patientId, CancellationToken ct);
    Task<Guid> CreateAsync(string phoneNumber, string? fullName, short? age, string? gender, string preferredLanguage, DateTime nowUtc, CancellationToken ct);
    Task LinkToTenantAsync(Guid patientId, Guid tenantId, DateTime nowUtc, CancellationToken ct);
    Task<bool> IsLinkedToTenantAsync(Guid patientId, Guid tenantId, CancellationToken ct);
}

/// <summary>
/// Write-side doctor provisioning. A doctor is tenant-scoped (NOT cross-tenant like a patient), so the row
/// carries the caller's <c>tenant_id</c> and the INSERT runs inside the command's tenant-scoped UoW
/// transaction (RLS-honoured). The DB owns the defaults for <c>role</c> ('doctor') and <c>qualifications</c>
/// ('[]'::jsonb); the repository only sets the columns the caller actually supplied. A thin enough surface
/// (a single guarded INSERT against an existing schema) that a full Repository aggregate is not warranted —
/// it earns its place purely to keep raw SQL out of the Application handler.
/// </summary>
public interface IDoctorRepository
{
    Task<Guid> CreateAsync(NewDoctor doctor, Guid tenantId, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// REPLACES the doctor's entire weekly schedule: deletes all existing <c>docslot.doctor_schedules</c> rows
    /// for the doctor then inserts the supplied blocks, all within the command's UnitOfWork transaction
    /// (atomic — RLS-honoured). Passing an empty list clears the schedule. The DB CHECK constraints
    /// (chk_schedule_time, chk_break_time) back the FluentValidation rules. Returns rows inserted.
    /// </summary>
    Task<int> ReplaceSchedulesAsync(Guid doctorId, IReadOnlyList<ScheduleBlock> blocks, CancellationToken ct);

    /// <summary>
    /// UPSERTs a date-specific override (ON CONFLICT (doctor_id, override_date) DO UPDATE). Runs inside the
    /// command transaction. Returns the override_id (existing or newly generated).
    /// </summary>
    Task<Guid> UpsertOverrideAsync(Guid doctorId, ScheduleOverride ovr, DateTime nowUtc, CancellationToken ct);

    /// <summary>Deletes one override by id, scoped to the doctor. Returns true when a row was removed.</summary>
    Task<bool> DeleteOverrideAsync(Guid doctorId, Guid overrideId, CancellationToken ct);

    /// <summary>
    /// Updates ONLY the supplied (non-null) WHITELISTED columns on <c>docslot.doctors</c> (full_name,
    /// display_name, specialization, department_id, consultation_fee, phone, email, is_active,
    /// is_accepting_new_patients). Immutable columns (tenant_id, user_id, nmc_*) are never named. Scoped to the
    /// doctor + tenant + not-deleted; runs inside the command transaction. Returns true when a row was updated.
    /// </summary>
    Task<bool> UpdateAsync(Guid doctorId, Guid tenantId, DoctorUpdate update, DateTime nowUtc, CancellationToken ct);

    /// <summary>
    /// SOFT-deletes the doctor (sets <c>deleted_at = nowUtc</c>; never a hard DELETE), scoped to doctor +
    /// tenant + not-already-deleted, inside the command transaction. Returns true when a row was soft-deleted.
    /// </summary>
    Task<bool> SoftDeleteAsync(Guid doctorId, Guid tenantId, DateTime nowUtc, CancellationToken ct);
}

/// <summary>Insert shape for one <c>docslot.doctor_schedules</c> block (no PK — the DB generates schedule_id).</summary>
public sealed record ScheduleBlock(
    short DayOfWeek,
    TimeOnly StartTime,
    TimeOnly EndTime,
    short SlotDurationMinutes,
    short MaxPatientsPerSlot,
    TimeOnly? BreakStartTime,
    TimeOnly? BreakEndTime,
    bool IsActive);

/// <summary>Upsert shape for one <c>docslot.schedule_overrides</c> row.</summary>
public sealed record ScheduleOverride(
    DateOnly OverrideDate,
    bool IsBlocked,
    TimeOnly? CustomStartTime,
    TimeOnly? CustomEndTime,
    string? Reason);

/// <summary>
/// Whitelisted doctor-update input. Every column is nullable; a null leaves that column UNTOUCHED. Only these
/// columns can ever be mutated through the update path — the immutable identity/regulatory columns are absent
/// by construction. <c>Email</c> is the citext contact column; it is never silently mutable beyond this list.
/// </summary>
public sealed record DoctorUpdate(
    string? FullName,
    string? DisplayName,
    string? Specialization,
    Guid? DepartmentId,
    decimal? ConsultationFee,
    string? Phone,
    string? Email,
    bool? IsActive,
    bool? IsAcceptingNewPatients);

/// <summary>Provisioning input for a new <c>docslot.doctors</c> row — only columns that exist on the table.</summary>
public sealed record NewDoctor(
    string FullName,
    string? DisplayName,
    Guid? DepartmentId,
    string? Specialization,
    string? Qualifications,        // pre-serialized JSON array string (e.g. "[]"); null → DB default '[]'::jsonb
    decimal? ConsultationFee,
    string? Gender,                // snake_case DB token ('male'|'female'|'other'|'prefer_not_say') or null
    string? Phone,
    string? Email,
    short? ExperienceYears,
    bool IsAcceptingNewPatients);
