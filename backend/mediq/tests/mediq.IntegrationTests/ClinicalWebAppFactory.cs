using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice-03b fixture. Boots the real API (running as the least-privilege <c>docslot_app</c> role, so RLS is
/// ENFORCED) and seeds: TWO tenants (A = the caller's, B = a foreign tenant for the RLS cross-tenant test),
/// a super_admin+tenant_owner user, a consented patient linked to both tenants, a booking in tenant A, a
/// doctor, and an active ABDM consent in tenant A. Setup/teardown use a privileged role (gtmkumar) since
/// they seed/clean across tenants and PHI tables; the API under test uses docslot_app.
/// </summary>
public sealed class ClinicalWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminConnString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string AdminPassword = "Sup3rSecret!";

    public Guid TenantA { get; } = Guid.NewGuid();
    public Guid TenantB { get; } = Guid.NewGuid();
    public Guid AdminUserId { get; } = Guid.NewGuid();
    public Guid PatientId { get; } = Guid.NewGuid();
    public Guid DoctorId { get; } = Guid.NewGuid();
    public Guid BookingId { get; } = Guid.NewGuid();
    public Guid SlotId { get; } = Guid.NewGuid();
    public Guid AbdmConsentId { get; } = Guid.NewGuid();
    public string AdminEmail { get; } = $"slice03b.admin+{Guid.NewGuid():N}@docslot.test";
    public string PatientPhone { get; } = $"+9196{Random.Shared.Next(10000000, 99999999)}";

    // Local-filesystem blob root for the test API, so the blob test can read the stored bytes off disk and
    // prove the PHI artifact is ciphertext at rest. Cleaned up in DisposeAsync.
    public string BlobRoot { get; } = Path.Combine(Path.GetTempPath(), $"docslot-blobtest-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("BlobStorage:Provider", "local_fs");
        builder.UseSetting("BlobStorage:RootPath", BlobRoot);
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnString);
        await conn.OpenAsync();

        foreach (var (tid, code) in new[] { (TenantA, "a"), (TenantB, "b") })
            await Exec(conn,
                """
                INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
                VALUES (@id, @code, 'Slice03b', 'Slice03b', 'hospital', @code||'@docslot.test', '+919600000000', 'active')
                ON CONFLICT (tenant_id) DO NOTHING
                """,
                ("id", tid), ("code", $"s03b-{code}-{tid.ToString()[..8]}"));

        await Exec(conn,
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, is_platform_user, created_at, updated_at)
            VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Slice03b Admin', true, true, NOW(), NOW())
            ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
            """,
            ("id", AdminUserId), ("email", AdminEmail), ("pwd", AdminPassword));
        // tenant_owner in BOTH tenants (so the pool-safety test can scope to either) + super_admin platform.
        foreach (var tid in new[] { TenantA, TenantB })
            await Exec(conn, """
                INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
                SELECT gen_random_uuid(), @uid, @tid, r.role_id, false, NOW()
                FROM platform.roles r WHERE r.role_key='tenant_owner' AND r.is_system ON CONFLICT DO NOTHING
                """, ("uid", AdminUserId), ("tid", tid));
        await Exec(conn, """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, NULL, r.role_id, false, NOW()
            FROM platform.roles r WHERE r.role_key='super_admin' AND r.is_system ON CONFLICT DO NOTHING
            """, ("uid", AdminUserId));
        // Break-glass perm rides on the 'doctor' role per the 05 seed (tenant-scoped); grant the admin a
        // doctor assignment in tenant A so the break-glass override test can issue an emergency grant.
        await Exec(conn, """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, @tid, r.role_id, false, NOW()
            FROM platform.roles r WHERE r.role_key='doctor' AND r.is_system ON CONFLICT DO NOTHING
            """, ("uid", AdminUserId), ("tid", TenantA));

        // Facility + doctor + slot + booking in tenant A.
        await Exec(conn, "INSERT INTO docslot.healthcare_facilities (facility_id, tenant_id, facility_type, created_at, updated_at) VALUES (gen_random_uuid(), @t, 'hospital', NOW(), NOW()) ON CONFLICT (tenant_id) DO NOTHING", ("t", TenantA));
        await Exec(conn, "INSERT INTO docslot.doctors (doctor_id, tenant_id, full_name, is_active, is_accepting_new_patients, created_at, updated_at) VALUES (@id, @t, 'Dr 03b', true, true, NOW(), NOW()) ON CONFLICT (doctor_id) DO NOTHING", ("id", DoctorId), ("t", TenantA));
        await Exec(conn, "INSERT INTO docslot.time_slots (slot_id, tenant_id, doctor_id, slot_date, start_time, end_time, status, current_count, max_count, created_at) VALUES (@id, @t, @doc, CURRENT_DATE, '09:00','09:15','booked',1,1, NOW()) ON CONFLICT DO NOTHING", ("id", SlotId), ("t", TenantA), ("doc", DoctorId));

        // Consented patient linked to BOTH tenants (so the cross-tenant RLS test uses the same patient).
        await Exec(conn,
            """
            INSERT INTO docslot.patients (patient_id, phone_number, full_name, consent_given_at, consent_version, is_active, created_at, updated_at)
            VALUES (@id, @phone, 'Clinical Patient', NOW(), 'v1', true, NOW(), NOW()) ON CONFLICT (phone_number) DO NOTHING
            """,
            ("id", PatientId), ("phone", PatientPhone));
        foreach (var tid in new[] { TenantA, TenantB })
            await Exec(conn, "INSERT INTO docslot.patient_tenant_links (link_id, patient_id, tenant_id, first_visit_at, last_visit_at, total_visits) VALUES (gen_random_uuid(), @p, @t, NOW(), NOW(), 0) ON CONFLICT DO NOTHING", ("p", PatientId), ("t", tid));

        await Exec(conn,
            """
            INSERT INTO docslot.bookings (booking_id, tenant_id, slot_id, patient_id, doctor_id, status, booked_via, booked_for, booked_at, updated_at)
            VALUES (@id, @t, @s, @p, @doc, 'completed', 'dashboard', 'self', NOW(), NOW()) ON CONFLICT DO NOTHING
            """,
            ("id", BookingId), ("t", TenantA), ("s", SlotId), ("p", PatientId), ("doc", DoctorId));

        // Active ABDM consent in tenant A.
        await Exec(conn,
            """
            INSERT INTO docslot.abdm_consents (consent_id, patient_id, requesting_tenant_id, abdm_consent_request_id, purpose, expires_at, status, granted_at)
            VALUES (@id, @p, @t, 'req-03b', 'treatment', NOW() + INTERVAL '30 days', 'granted', NOW()) ON CONFLICT DO NOTHING
            """,
            ("id", AbdmConsentId), ("p", PatientId), ("t", TenantA));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnString);
        await conn.OpenAsync();
        foreach (var t in new[] { TenantA, TenantB })
        {
            await Exec(conn, "DELETE FROM docslot.abdm_health_records WHERE tenant_id=@t", ("t", t));
            await Exec(conn, "DELETE FROM docslot.abdm_consents WHERE requesting_tenant_id=@t", ("t", t));
            await Exec(conn, "DELETE FROM docslot.prescriptions WHERE tenant_id=@t", ("t", t));
            await Exec(conn, "DELETE FROM docslot.lab_reports WHERE tenant_id=@t", ("t", t));
            await Exec(conn, "DELETE FROM docslot.patient_medical_history WHERE tenant_id=@t", ("t", t));
            await Exec(conn, "DELETE FROM docslot.bookings WHERE tenant_id=@t", ("t", t));
            await Exec(conn, "DELETE FROM docslot.time_slots WHERE tenant_id=@t", ("t", t));
            await Exec(conn, "DELETE FROM docslot.doctors WHERE tenant_id=@t", ("t", t));
            await Exec(conn, "DELETE FROM docslot.healthcare_facilities WHERE tenant_id=@t", ("t", t));
            await Exec(conn, "DELETE FROM docslot.patient_tenant_links WHERE tenant_id=@t", ("t", t));
            // key_usage_log FK-references encryption_keys → delete usage first.
            await Exec(conn, "DELETE FROM platform.key_usage_log WHERE key_id IN (SELECT key_id FROM platform.encryption_keys WHERE tenant_id=@t)", ("t", t));
            await Exec(conn, "DELETE FROM platform.encryption_keys WHERE tenant_id=@t", ("t", t));
            // break_glass_grants.purpose_log_id FKs purpose_of_use_log → delete grants first (defensive).
            await Exec(conn, "DELETE FROM platform.break_glass_grants WHERE tenant_id=@t", ("t", t));
            await Exec(conn, "DELETE FROM platform.purpose_of_use_log WHERE tenant_id=@t", ("t", t));
        }
        await Exec(conn, "DELETE FROM docslot.patients WHERE patient_id=@p", ("p", PatientId));
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id=@u", ("u", AdminUserId));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id=@u", ("u", AdminUserId));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email=@e", ("e", AdminEmail));
        await Exec(conn, "UPDATE platform.users SET deleted_at=NOW(), is_active=false, email=@a WHERE user_id=@u", ("a", $"del+{AdminUserId}@s03b.test"), ("u", AdminUserId));
        foreach (var t in new[] { TenantA, TenantB })
            await Exec(conn, "UPDATE platform.tenants SET deleted_at=NOW(), status='archived' WHERE tenant_id=@t", ("t", t));
        if (Directory.Exists(BlobRoot)) Directory.Delete(BlobRoot, recursive: true);
        await base.DisposeAsync();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
