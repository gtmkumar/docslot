using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice-05 fixture. Boots the real API against the live canonical DB and seeds a super_admin user (holds
/// all platform.* security perms via the seed) + a patient with a tenant link, so the security/compliance
/// endpoints and the field-encryption services can be exercised end-to-end. Cleanup soft-deletes audited
/// rows and removes the transient encryption keys/usage created by the test.
/// </summary>
public sealed class SecurityWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string AdminPassword = "Sup3rSecret!";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid AdminUserId { get; } = Guid.NewGuid();
    public Guid PatientId { get; } = Guid.NewGuid();
    public string AdminEmail { get; } = $"slice05.admin+{Guid.NewGuid():N}@docslot.test";
    public string PatientPhone { get; } = $"+9197{Random.Shared.Next(10000000, 99999999)}";

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (@id, @code, 'Slice05 Hospital', 'Slice05 Hospital', 'hospital', 'slice05@docslot.test', '+919500000000', 'active')
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            ("id", TenantId), ("code", $"slice05-{TenantId.ToString()[..8]}"));

        await Exec(conn,
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, is_platform_user, created_at, updated_at)
            VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Slice05 Admin', true, true, NOW(), NOW())
            ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
            """,
            ("id", AdminUserId), ("email", AdminEmail), ("pwd", AdminPassword));

        // Platform super_admin (holds all platform.* security perms) + tenant_owner (for break-glass tenant ctx).
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, NULL, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = 'super_admin' AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", AdminUserId));
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, @tid, r.role_id, false, NOW()
            FROM platform.roles r WHERE r.role_key = 'tenant_owner' AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", AdminUserId), ("tid", TenantId));
        // Break-glass perm rides on the 'doctor' role per seed; grant the admin a doctor assignment too.
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, @tid, r.role_id, false, NOW()
            FROM platform.roles r WHERE r.role_key = 'doctor' AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", AdminUserId), ("tid", TenantId));

        await Exec(conn,
            """
            INSERT INTO docslot.patients (patient_id, phone_number, full_name, is_active, created_at, updated_at)
            VALUES (@id, @phone, 'Erasure Subject', true, NOW(), NOW())
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
        // Remove test-created key usage + keys, export/breach/cert rows, then soft-handle audited entities.
        await Exec(conn, "DELETE FROM platform.key_usage_log WHERE key_id IN (SELECT key_id FROM platform.encryption_keys WHERE tenant_id = @t)", ("t", TenantId));
        await Exec(conn, "DELETE FROM platform.deletion_certificates WHERE subject_phone = @p", ("p", PatientPhone));
        await Exec(conn, "DELETE FROM platform.data_deletion_requests WHERE subject_phone = @p", ("p", PatientPhone));
        await Exec(conn, "DELETE FROM platform.data_export_requests WHERE subject_phone = @p", ("p", PatientPhone));
        await Exec(conn, "DELETE FROM platform.encryption_keys WHERE tenant_id = @t", ("t", TenantId));
        // break_glass_grants.purpose_log_id FKs purpose_of_use_log → delete grants first.
        await Exec(conn, "DELETE FROM platform.break_glass_grants WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM platform.purpose_of_use_log WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.patient_tenant_links WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.patients WHERE patient_id = @p", ("p", PatientId));
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = @u", ("u", AdminUserId));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = @u", ("u", AdminUserId));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = @e", ("e", AdminEmail));
        await Exec(conn, "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
            ("anon", $"deleted+{AdminUserId}@slice05.test"), ("u", AdminUserId));
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
