using System.Collections.Concurrent;
using mediq.Application.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice-03 fixture. Boots the real API against the live canonical DB and seeds a complete booking-core
/// graph: tenant (hospital) + super_admin user + facility + department + doctor + an available time_slot +
/// a consented patient linked to the tenant. The <see cref="IWebhookPublisher"/> is swapped for a recorder
/// so a test can assert booking actions emit integration events. Cleanup soft-deletes audited rows.
/// </summary>
public sealed class DocslotWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string AdminPassword = "Sup3rSecret!";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid AdminUserId { get; } = Guid.NewGuid();
    public Guid DoctorId { get; } = Guid.NewGuid();
    public Guid DepartmentId { get; } = Guid.NewGuid();
    public Guid SlotId { get; } = Guid.NewGuid();
    public Guid SecondSlotId { get; } = Guid.NewGuid();
    public Guid PatientId { get; } = Guid.NewGuid();
    public string AdminEmail { get; } = $"slice03.admin+{Guid.NewGuid():N}@docslot.test";
    public string PatientPhone { get; } = $"+9198{Random.Shared.Next(10000000, 99999999)}";
    public static readonly DateOnly SlotDate = DateOnly.FromDateTime(DateTime.UtcNow.Date);

    public static readonly RecordingWebhookPublisher Publisher = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            var original = services.Single(d => d.ServiceType == typeof(IWebhookPublisher));
            services.Remove(original);
            services.AddScoped<IWebhookPublisher>(_ => Publisher);
        });
    }

    public async Task InitializeAsync()
    {
        Publisher.Reset();
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (@id, @code, 'Slice03 Hospital', 'Slice03 Hospital', 'hospital', 'slice03@docslot.test', '+919000000000', 'active')
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            ("id", TenantId), ("code", $"slice03-{TenantId.ToString()[..8]}"));

        await Exec(conn,
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, is_platform_user, created_at, updated_at)
            VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Slice03 Admin', true, true, NOW(), NOW())
            ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
            """,
            ("id", AdminUserId), ("email", AdminEmail), ("pwd", AdminPassword));

        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = 'tenant_owner' AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", AdminUserId), ("tid", TenantId));

        // Facility seeded with the Settings surface fields: business_hours + appointment_settings jsonb,
        // a verified WhatsApp connection (verified_at set, phone id present), and a verified HFR id.
        await Exec(conn,
            """
            INSERT INTO docslot.healthcare_facilities
              (facility_id, tenant_id, facility_type, specialty_focus,
               whatsapp_business_phone_id, whatsapp_access_token, whatsapp_verified_at,
               hfr_id, hfr_status, business_hours, appointment_settings, created_at, updated_at)
            VALUES (gen_random_uuid(), @tid, 'hospital', 'multi_specialty',
               'PNID_SLICE_SETTINGS', 'super-secret-token-never-leak', NOW() - interval '10 days',
               'HFR-TEST-0001', 'verified',
               '{"mon":{"open":"09:00","close":"18:00","closed":false},
                 "tue":{"open":"09:00","close":"18:00","closed":false},
                 "wed":{"open":"09:00","close":"18:00","closed":false},
                 "thu":{"open":"09:00","close":"18:00","closed":false},
                 "fri":{"open":"09:00","close":"18:00","closed":false},
                 "sat":{"open":"09:00","close":"14:00","closed":false},
                 "sun":{"open":null,"close":null,"closed":true}}'::jsonb,
               '{"slotDurationMinutes":15,"bookingCutoffHours":2,"autoConfirm":true,
                 "maxAdvanceDays":30,"allowOverbooking":false,"reminderHoursBefore":24,
                 "noShowGraceMinutes":15}'::jsonb,
               NOW(), NOW())
            ON CONFLICT (tenant_id) DO UPDATE
              SET specialty_focus = EXCLUDED.specialty_focus,
                  whatsapp_business_phone_id = EXCLUDED.whatsapp_business_phone_id,
                  whatsapp_access_token = EXCLUDED.whatsapp_access_token,
                  whatsapp_verified_at = EXCLUDED.whatsapp_verified_at,
                  hfr_id = EXCLUDED.hfr_id,
                  hfr_status = EXCLUDED.hfr_status,
                  business_hours = EXCLUDED.business_hours,
                  appointment_settings = EXCLUDED.appointment_settings
            """,
            ("tid", TenantId));

        await Exec(conn,
            """
            INSERT INTO docslot.departments (department_id, tenant_id, name, is_active, created_at, updated_at)
            VALUES (@id, @tid, 'General Medicine', true, NOW(), NOW())
            ON CONFLICT (tenant_id, name) DO NOTHING
            """,
            ("id", DepartmentId), ("tid", TenantId));

        await Exec(conn,
            """
            INSERT INTO docslot.doctors (doctor_id, tenant_id, full_name, department_id, specialization, consultation_fee, is_active, is_accepting_new_patients, created_at, updated_at)
            VALUES (@id, @tid, 'Dr Slice03', @dep, 'General', 500.00, true, true, NOW(), NOW())
            ON CONFLICT (doctor_id) DO NOTHING
            """,
            ("id", DoctorId), ("tid", TenantId), ("dep", DepartmentId));

        // Two available slots (one for the happy path, one spare).
        foreach (var (sid, start) in new[] { (SlotId, new TimeOnly(10, 0)), (SecondSlotId, new TimeOnly(10, 15)) })
            await Exec(conn,
                """
                INSERT INTO docslot.time_slots (slot_id, tenant_id, doctor_id, slot_date, start_time, end_time, status, current_count, max_count, created_at)
                VALUES (@id, @tid, @doc, @date, @start, @end, 'available', 0, 1, NOW())
                ON CONFLICT (doctor_id, slot_date, start_time) DO NOTHING
                """,
                ("id", sid), ("tid", TenantId), ("doc", DoctorId), ("date", SlotDate),
                ("start", start), ("end", start.AddMinutes(15)));

        // A consented patient linked to the tenant.
        await Exec(conn,
            """
            INSERT INTO docslot.patients (patient_id, phone_number, full_name, age, gender, preferred_language, consent_given_at, consent_version, is_active, created_at, updated_at)
            VALUES (@id, @phone, 'Test Patient', 35, 'male', 'en', NOW(), 'v1', true, NOW(), NOW())
            ON CONFLICT (phone_number) DO NOTHING
            """,
            ("id", PatientId), ("phone", PatientPhone));

        await Exec(conn,
            """
            INSERT INTO docslot.patient_tenant_links (link_id, patient_id, tenant_id, first_visit_at, last_visit_at, total_visits)
            VALUES (gen_random_uuid(), @pid, @tid, NOW(), NOW(), 0)
            ON CONFLICT (patient_id, tenant_id) DO NOTHING
            """,
            ("pid", PatientId), ("tid", TenantId));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        // Children first (FKs). Bookings cascade history/holds/tokens; remove dependent rows then soft-handle audited.
        await Exec(conn, "DELETE FROM platform.idempotency_keys WHERE tenant_scope = @t", ("t", TenantId.ToString()));
        await Exec(conn, "DELETE FROM docslot.slot_holds WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.opd_tokens WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.booking_status_history WHERE booking_id IN (SELECT booking_id FROM docslot.bookings WHERE tenant_id=@t)", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.bookings WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.time_slots WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.doctors WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.departments WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.healthcare_facilities WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.patient_tenant_links WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.patients WHERE patient_id = @p", ("p", PatientId));
        await Exec(conn, "DELETE FROM platform.purpose_of_use_log WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = @u", ("u", AdminUserId));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = @u", ("u", AdminUserId));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = @e", ("e", AdminEmail));
        await Exec(conn, "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
            ("anon", $"deleted+{AdminUserId}@slice03.test"), ("u", AdminUserId));
        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = @t", ("t", TenantId));
        await base.DisposeAsync();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>Records every integration event published, so a test can assert booking actions emit events.</summary>
public sealed class RecordingWebhookPublisher : IWebhookPublisher
{
    public ConcurrentQueue<IntegrationEvent> Published { get; } = new();
    public void Reset() => Published.Clear();

    public Task<IReadOnlyList<Guid>> PublishAsync(IntegrationEvent integrationEvent, CancellationToken ct)
    {
        Published.Enqueue(integrationEvent);
        return Task.FromResult<IReadOnlyList<Guid>>([]);
    }
}
