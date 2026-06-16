using mediq.Domain.Docslot;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;

namespace mediq.Application.Abstractions;

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
    Task<PatientDetailDto?> GetDetailAsync(Guid tenantId, Guid patientId, CancellationToken ct);
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
}

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
