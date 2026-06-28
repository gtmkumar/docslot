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

    public async Task<int> ReplaceSchedulesAsync(Guid doctorId, IReadOnlyList<ScheduleBlock> blocks, CancellationToken ct)
    {
        // REPLACE without a physical DELETE: the least-privilege docslot_app role has SELECT/INSERT/UPDATE but
        // NOT DELETE on docslot.doctor_schedules (10_roles_grants.sql grants DELETE only to a narrow list), and
        // the platform principle is "soft delete everywhere — never physically DELETE". So we DEACTIVATE every
        // existing block (is_active = false; UPDATE is granted) then INSERT the supplied blocks. The slot
        // generator reads only is_active = true blocks, and the GET projection filters is_active = true, so the
        // deactivated rows are invisible and never accumulate into availability. Both statements run on the
        // command's UnitOfWork transaction (atomic; RLS-honoured). An empty list simply clears the schedule.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE docslot.doctor_schedules SET is_active = false WHERE doctor_id = @p0 AND is_active = true",
            [new NpgsqlParameter("@p0", doctorId)], ct);

        var inserted = 0;
        foreach (var b in blocks)
        {
            // The DB CHECK constraints (chk_schedule_time end>start; chk_break_time pair-or-null + end>start)
            // are the backstop behind the validator. A raw CHECK violation is a PostgresException (NOT wrapped
            // in DbUpdateException), so it would bypass the middleware's SQLSTATE mapping and 500 — translate
            // known SQLSTATEs here into a ValidationException → 422.
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO docslot.doctor_schedules
                        (doctor_id, day_of_week, start_time, end_time, slot_duration_minutes,
                         max_patients_per_slot, break_start_time, break_end_time, is_active)
                    VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8)
                    """,
                    [
                        new NpgsqlParameter("@p0", doctorId),
                        new NpgsqlParameter("@p1", b.DayOfWeek),
                        new NpgsqlParameter("@p2", b.StartTime),
                        new NpgsqlParameter("@p3", b.EndTime),
                        new NpgsqlParameter("@p4", b.SlotDurationMinutes),
                        new NpgsqlParameter("@p5", b.MaxPatientsPerSlot),
                        new NpgsqlParameter("@p6", (object?)b.BreakStartTime ?? DBNull.Value),
                        new NpgsqlParameter("@p7", (object?)b.BreakEndTime ?? DBNull.Value),
                        new NpgsqlParameter("@p8", b.IsActive),
                    ], ct);
                inserted++;
            }
            catch (PostgresException ex) when (ex.SqlState is "23514" or "22P02" or "23505")
            {
                throw new mediq.Utilities.Exceptions.ValidationException(
                    "A schedule block violates a data constraint (check start<end, break pair, and value ranges).", ex);
            }
        }

        return inserted;
    }

    public async Task<Guid> UpsertOverrideAsync(Guid doctorId, ScheduleOverride ovr, DateTime nowUtc, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        try
        {
            // Idempotent per (doctor_id, override_date) via the UNIQUE constraint. RETURNING gives the
            // surviving row's id (new on insert, existing on conflict-update). Raw SQL → translate CHECK/format
            // SQLSTATEs to 422 (custom_end_time has no CHECK in the schema but bad TIME formats surface as 22P02).
            var rows = await db.Database.SqlQueryRaw<GuidRow>(
                    """
                    INSERT INTO docslot.schedule_overrides
                        (override_id, doctor_id, override_date, is_blocked, custom_start_time, custom_end_time, reason, created_at)
                    VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7)
                    ON CONFLICT (doctor_id, override_date) DO UPDATE
                        SET is_blocked = EXCLUDED.is_blocked,
                            custom_start_time = EXCLUDED.custom_start_time,
                            custom_end_time = EXCLUDED.custom_end_time,
                            reason = EXCLUDED.reason
                    RETURNING override_id AS "Value"
                    """,
                    new NpgsqlParameter("@p0", id),
                    new NpgsqlParameter("@p1", doctorId),
                    new NpgsqlParameter("@p2", ovr.OverrideDate),
                    new NpgsqlParameter("@p3", ovr.IsBlocked),
                    new NpgsqlParameter("@p4", (object?)ovr.CustomStartTime ?? DBNull.Value),
                    new NpgsqlParameter("@p5", (object?)ovr.CustomEndTime ?? DBNull.Value),
                    new NpgsqlParameter("@p6", (object?)ovr.Reason ?? DBNull.Value),
                    new NpgsqlParameter("@p7", nowUtc))
                .ToListAsync(ct);
            return rows.FirstOrDefault()?.Value ?? id;
        }
        catch (PostgresException ex) when (ex.SqlState is "23514" or "22P02")
        {
            throw new mediq.Utilities.Exceptions.ValidationException("The override violates a data constraint.", ex);
        }
    }

    public async Task<bool> DeleteOverrideAsync(Guid doctorId, Guid overrideId, CancellationToken ct)
    {
        // "Delete" without a physical DELETE: the least-privilege docslot_app role is NOT granted DELETE on
        // docslot.schedule_overrides (10_roles_grants.sql grants DELETE only to a narrow list; this table is not
        // on it — see the flag in this slice's report). So we NEUTRALIZE the override to a no-op tombstone via
        // UPDATE (granted): is_blocked = false + custom times NULL. The slot generator treats such a row as
        // "no override" (it only acts on is_blocked = true OR a non-null custom_start_time — see
        // docslot.generate_time_slots), and the overrides read filters these no-op rows out, so the override is
        // logically gone for both availability and the list. Scoped to override_id AND doctor_id so a tenant can
        // never neutralize another doctor's override by id alone. UPDATE only a row that is still "live" (not
        // already a tombstone) so a repeat delete correctly reports not-found.
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE docslot.schedule_overrides
            SET is_blocked = false, custom_start_time = NULL, custom_end_time = NULL,
                reason = '__deleted__'
            WHERE override_id = @p0 AND doctor_id = @p1
              AND NOT (is_blocked = false AND custom_start_time IS NULL AND reason = '__deleted__')
            """,
            [new NpgsqlParameter("@p0", overrideId), new NpgsqlParameter("@p1", doctorId)], ct);
        return affected > 0;
    }

    public async Task<bool> UpdateAsync(Guid doctorId, Guid tenantId, DoctorUpdate update, DateTime nowUtc, CancellationToken ct)
    {
        // WHITELIST: only these columns can ever be set here. Immutable columns (tenant_id, user_id, nmc_*) are
        // never named, so they can't be silently mutated. Build the SET list dynamically so a null field is
        // left UNTOUCHED. The WHERE pins doctor_id + tenant_id + not-deleted (cross-tenant + soft-delete safe).
        var sets = new List<string>();
        var ps = new List<NpgsqlParameter>
        {
            new("@p_id", doctorId),
            new("@p_tenant", tenantId),
            new("@p_now", nowUtc),
        };

        void Set(string column, string param, object? value)
        {
            if (value is null) return;
            sets.Add($"{column} = {param}");
            ps.Add(new NpgsqlParameter(param, value));
        }

        Set("full_name", "@p_full_name", update.FullName);
        Set("display_name", "@p_display_name", update.DisplayName);
        Set("specialization", "@p_specialization", update.Specialization);
        Set("department_id", "@p_department_id", update.DepartmentId);
        Set("consultation_fee", "@p_consultation_fee", update.ConsultationFee);
        Set("phone", "@p_phone", update.Phone);
        Set("email", "@p_email", update.Email);
        Set("is_active", "@p_is_active", update.IsActive);
        Set("is_accepting_new_patients", "@p_accepting", update.IsAcceptingNewPatients);

        if (sets.Count == 0) return false;   // nothing to change (validator already rejects this)

        sets.Add("updated_at = @p_now");
        var sql = $"UPDATE docslot.doctors SET {string.Join(", ", sets)} " +
                  "WHERE doctor_id = @p_id AND tenant_id = @p_tenant AND deleted_at IS NULL";

        try
        {
            var affected = await db.Database.ExecuteSqlRawAsync(sql, ps, ct);
            return affected > 0;
        }
        catch (PostgresException ex) when (ex.SqlState is "23514" or "22P02" or "23505" or "23503")
        {
            // bad gender/role enum (none here), duplicate citext email, bad FK department, etc. → 422 not 500.
            throw new mediq.Utilities.Exceptions.ValidationException("The doctor update violates a data constraint.", ex);
        }
    }

    public async Task<bool> SoftDeleteAsync(Guid doctorId, Guid tenantId, DateTime nowUtc, CancellationToken ct)
    {
        // SOFT delete (deleted_at = now) — never a physical DELETE. Scoped to doctor + tenant + not-already-deleted.
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE docslot.doctors
            SET deleted_at = @p_now, is_active = false, updated_at = @p_now
            WHERE doctor_id = @p_id AND tenant_id = @p_tenant AND deleted_at IS NULL
            """,
            [
                new NpgsqlParameter("@p_now", nowUtc),
                new NpgsqlParameter("@p_id", doctorId),
                new NpgsqlParameter("@p_tenant", tenantId),
            ], ct);
        return affected > 0;
    }

    private sealed record GuidRow(Guid Value);
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
