using System.Text.Json;
using mediq.Application.Abstractions;
using mediq.Domain.Docslot;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Docslot;

/// <summary>
/// Patient identity (cross-tenant by phone) + tenant linkage (<c>docslot.patients</c> /
/// <c>patient_tenant_links</c>). NOTE: <c>docslot.patients</c> is intentionally cross-tenant (phone = global
/// identity), so lookups by phone are NOT tenant-scoped; tenant access is mediated by the link table.
/// </summary>
public sealed class PatientRepository(PlatformDbContext db) : IPatientRepository
{
    public Task<Patient?> GetByPhoneAsync(string phoneNumber, CancellationToken ct) =>
        db.Patients.FirstOrDefaultAsync(p => p.PhoneNumber == phoneNumber && p.DeletedAt == null, ct);

    public Task<Patient?> GetByIdAsync(Guid patientId, CancellationToken ct) =>
        db.Patients.FirstOrDefaultAsync(p => p.PatientId == patientId && p.DeletedAt == null, ct);

    public async Task<Guid> CreateAsync(string phoneNumber, string? fullName, short? age, string? gender, string preferredLanguage, DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO docslot.patients (patient_id, phone_number, full_name, age, gender, preferred_language, is_active, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, true, @p6, @p6)
            ON CONFLICT (phone_number) DO NOTHING
            """,
            new NpgsqlParameter("@p0", id),
            new NpgsqlParameter("@p1", phoneNumber),
            new NpgsqlParameter("@p2", (object?)fullName ?? DBNull.Value),
            new NpgsqlParameter("@p3", (object?)age ?? DBNull.Value),
            new NpgsqlParameter("@p4", (object?)gender ?? DBNull.Value),
            new NpgsqlParameter("@p5", preferredLanguage),
            new NpgsqlParameter("@p6", nowUtc));

        // If a race inserted the same phone first, return the existing id.
        var existing = await GetByPhoneAsync(phoneNumber, ct);
        return existing?.PatientId ?? id;
    }

    public Task LinkToTenantAsync(Guid patientId, Guid tenantId, DateTime nowUtc, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO docslot.patient_tenant_links (link_id, patient_id, tenant_id, first_visit_at, last_visit_at, total_visits)
            VALUES (gen_random_uuid(), @p0, @p1, @p2, @p2, 0)
            ON CONFLICT (patient_id, tenant_id) DO UPDATE SET last_visit_at = @p2
            """,
            new NpgsqlParameter("@p0", patientId), new NpgsqlParameter("@p1", tenantId), new NpgsqlParameter("@p2", nowUtc));

    public Task<bool> IsLinkedToTenantAsync(Guid patientId, Guid tenantId, CancellationToken ct) =>
        db.PatientTenantLinks.AsNoTracking().AnyAsync(l => l.PatientId == patientId && l.TenantId == tenantId, ct);
}

/// <summary>
/// Doctor provisioning (<c>docslot.doctors</c>). A doctor is tenant-scoped, so the INSERT runs through the
/// SAME <see cref="PlatformDbContext"/> the command's UnitOfWork transaction owns — the <c>SET LOCAL
/// app.tenant_id</c> set by the UoW behavior applies, and tenant_id is the caller's claim, never a header.
/// The INSERT only names columns the caller supplied; <c>role</c> (DEFAULT 'doctor'), <c>qualifications</c>
/// (DEFAULT '[]'::jsonb), <c>doctor_id</c> (gen_random_uuid()) and the timestamp defaults are left to the DB.
/// </summary>
public sealed class DoctorRepository(PlatformDbContext db) : IDoctorRepository
{
    public async Task<Guid> CreateAsync(NewDoctor doctor, Guid tenantId, DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();

        // Build the column/value list dynamically so omitted columns fall back to the DB defaults
        // (role → 'doctor', qualifications → '[]'::jsonb). qualifications, when supplied, is cast to jsonb.
        var columns = new List<string> { "doctor_id", "tenant_id", "full_name", "is_active", "is_accepting_new_patients", "created_at", "updated_at" };
        var values = new List<string> { "@p_id", "@p_tenant", "@p_full_name", "true", "@p_accepting", "@p_now", "@p_now" };
        var ps = new List<NpgsqlParameter>
        {
            new("@p_id", id),
            new("@p_tenant", tenantId),
            new("@p_full_name", doctor.FullName),
            new("@p_accepting", doctor.IsAcceptingNewPatients),
            new("@p_now", nowUtc),
        };

        void AddIfPresent(string column, string param, object? value)
        {
            if (value is null) return;
            columns.Add(column);
            values.Add(param);
            ps.Add(new NpgsqlParameter(param, value));
        }

        AddIfPresent("display_name", "@p_display_name", doctor.DisplayName);
        AddIfPresent("department_id", "@p_department_id", doctor.DepartmentId);
        AddIfPresent("specialization", "@p_specialization", doctor.Specialization);
        AddIfPresent("consultation_fee", "@p_consultation_fee", doctor.ConsultationFee);
        AddIfPresent("gender", "@p_gender", doctor.Gender);
        AddIfPresent("phone", "@p_phone", doctor.Phone);
        AddIfPresent("email", "@p_email", doctor.Email);
        AddIfPresent("experience_years", "@p_experience_years", doctor.ExperienceYears);

        // qualifications is jsonb — cast the parameter when supplied; otherwise leave it to the DB default.
        if (doctor.Qualifications is not null)
        {
            columns.Add("qualifications");
            values.Add("@p_qualifications::jsonb");
            ps.Add(new NpgsqlParameter("@p_qualifications", doctor.Qualifications));
        }

        var sql = $"INSERT INTO docslot.doctors ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)})";
        await db.Database.ExecuteSqlRawAsync(sql, ps, ct);
        return id;
    }
}

/// <summary>
/// Translates booking lifecycle transitions into platform integration events and publishes them through
/// the slice-02 <see cref="IWebhookPublisher"/> (sign → deliver → retry → outbox). This is the
/// Application-boundary translation; domain events never leave the service directly.
/// </summary>
public sealed class BookingEventPublisher(IWebhookPublisher webhooks, ICurrentUserContext ctx, IClock clock)
    : IBookingEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task PublishAsync(string eventType, Guid tenantId, Guid bookingId, string? bookingNumber, object payload, CancellationToken ct)
    {
        var envelope = new
        {
            event_type = eventType,
            tenant_id = tenantId,
            booking_id = bookingId,
            booking_number = bookingNumber,
            occurred_at = clock.UtcNow,
            data = payload,
        };
        var evt = new IntegrationEvent(
            Guid.CreateVersion7(), eventType, tenantId,
            JsonSerializer.Serialize(envelope, JsonOptions), ctx.CorrelationId, clock.UtcNow);

        return webhooks.PublishAsync(evt, ct);
    }
}
