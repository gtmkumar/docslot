using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice-13 fixture for the tenant_type-aware navigation tests. Seeds ONE user (super_admin platform +
/// tenant_owner in BOTH tenants, so the menu PERMISSION gate always passes and tenant_type is the only
/// differentiator) and TWO tenants of different tenant_type: a 'hospital' and a 'pathology_lab'. The user can
/// log in scoped to either tenant; GET /me/menus then resolves get_user_menus with that tenant's type.
/// Setup/teardown run as the privileged role (cross-tenant seed). Dedicated factory (NOT the shared
/// PlatformWebAppFactory) so the extra tenant/membership can't perturb the resolve-once query-count assertions.
/// </summary>
public sealed class MenuTenantTypeWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminConnString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string Password = "Sup3rSecret!";

    public Guid HospitalTenantId { get; } = Guid.NewGuid();
    public Guid LabTenantId { get; } = Guid.NewGuid();
    public Guid UserId { get; } = Guid.NewGuid();
    public string Email { get; } = $"slice13.menus+{Guid.NewGuid():N}@docslot.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnString);
        await conn.OpenAsync();

        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (@id, @code, 'Slice13 Hospital', 'Slice13 Hospital', 'hospital', @code||'@s13.test', '+919600000000', 'active')
            """,
            ("id", HospitalTenantId), ("code", $"s13-h-{HospitalTenantId.ToString()[..8]}"));
        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (@id, @code, 'Slice13 Lab', 'Slice13 Lab', 'pathology_lab', @code||'@s13.test', '+919600000001', 'active')
            """,
            ("id", LabTenantId), ("code", $"s13-l-{LabTenantId.ToString()[..8]}"));

        await Exec(conn,
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, is_platform_user, created_at, updated_at)
            VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Slice13 Menus User', true, true, NOW(), NOW())
            ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
            """,
            ("id", UserId), ("email", Email), ("pwd", Password));

        // super_admin (platform NULL) so every menu's permission gate passes → tenant_type is the sole filter.
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, NULL, r.role_id, false, NOW()
            FROM platform.roles r WHERE r.role_key='super_admin' AND r.is_system ON CONFLICT DO NOTHING
            """,
            ("uid", UserId));
        // tenant_owner in BOTH tenants so login can scope to either (membership) + an active tenant resolves.
        foreach (var tid in new[] { HospitalTenantId, LabTenantId })
            await Exec(conn,
                """
                INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
                SELECT gen_random_uuid(), @uid, @tid, r.role_id, false, NOW()
                FROM platform.roles r WHERE r.role_key='tenant_owner' AND r.is_system ON CONFLICT DO NOTHING
                """,
                ("uid", UserId), ("tid", tid));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnString);
        await conn.OpenAsync();
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id=@u", ("u", UserId));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id=@u", ("u", UserId));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email=@e", ("e", Email));
        await Exec(conn, "UPDATE platform.users SET deleted_at=NOW(), is_active=false, email=@a WHERE user_id=@u",
            ("a", $"del+{UserId}@s13.test"), ("u", UserId));
        foreach (var tid in new[] { HospitalTenantId, LabTenantId })
            await Exec(conn, "UPDATE platform.tenants SET deleted_at=NOW(), status='archived' WHERE tenant_id=@t", ("t", tid));
        await base.DisposeAsync();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
