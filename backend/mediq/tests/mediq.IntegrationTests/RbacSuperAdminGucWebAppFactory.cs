using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Fixture proving the <c>app.is_super_admin</c> GUC wiring (UnitOfWork sets it from
/// <c>platform.is_super_admin(user)</c> per transaction). Seeds, as the DB OWNER (RLS-exempt, setup only):
/// <list type="bullet">
///   <item>two tenants <b>A</b> (the admin's home) and <b>B</b> (a foreign tenant),</item>
///   <item>a <b>super-admin</b> user = platform <c>super_admin</c> (tenant NULL) PLUS <c>tenant_owner</c> in A
///   (so login resolves A and the request holds <c>tenant.users.read</c>),</item>
///   <item>a <b>control</b> user = <c>tenant_owner</c> in A only (no super_admin),</item>
///   <item>a uniquely-identifiable custom role that lives in tenant <b>B</b>.</item>
/// </list>
/// The app runs as <c>docslot_app</c> (NOBYPASSRLS), so a cross-tenant read of B's role succeeds ONLY when the
/// request set <c>app.is_super_admin=true</c> (the R1 <c>roles_read</c> policy → <c>rls_can_see_tenant</c>).
/// </summary>
public sealed class RbacSuperAdminGucWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string OwnerConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string Password = "Sup3rSecret!";

    public Guid TenantAId { get; } = Guid.NewGuid();
    public Guid TenantBId { get; } = Guid.NewGuid();
    public Guid SuperAdminUserId { get; } = Guid.NewGuid();
    public Guid ControlUserId { get; } = Guid.NewGuid();

    /// <summary>A custom role that belongs to tenant B — the cross-tenant visibility probe.</summary>
    public Guid TenantBRoleId { get; } = Guid.NewGuid();

    public string SuperAdminEmail { get; } = $"guc.super+{Guid.NewGuid():N}@docslot.test";
    public string ControlEmail { get; } = $"guc.control+{Guid.NewGuid():N}@docslot.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var (tid, code, name) in new[]
        {
            (TenantAId, $"guc-a-{TenantAId.ToString()[..8]}", "GUC Tenant A"),
            (TenantBId, $"guc-b-{TenantBId.ToString()[..8]}", "GUC Tenant B"),
        })
            await Exec(conn,
                """
                INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
                VALUES (@id, @code, @name, @name, 'hospital', 'guc@docslot.test', '+919766666666', 'active')
                ON CONFLICT (tenant_id) DO NOTHING
                """,
                ("id", tid), ("code", code), ("name", name));

        foreach (var (uid, email) in new[] { (SuperAdminUserId, SuperAdminEmail), (ControlUserId, ControlEmail) })
            await Exec(conn,
                """
                INSERT INTO platform.users (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, created_at, updated_at)
                VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'GUC Test User', true, true, false, NOW(), NOW())
                ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
                """,
                ("id", uid), ("email", email), ("pwd", Password));

        // super_admin at PLATFORM scope (tenant_id NULL) — the universal grantor.
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, NULL, r.role_id, false, NOW()
            FROM platform.roles r WHERE r.role_key = 'super_admin' AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", SuperAdminUserId));

        // Both users are tenant_owner IN tenant A (login resolves A; both hold tenant.users.read).
        foreach (var uid in new[] { SuperAdminUserId, ControlUserId })
            await Exec(conn,
                """
                INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
                SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
                FROM platform.roles r WHERE r.role_key = 'tenant_owner' AND r.is_system = true
                ON CONFLICT DO NOTHING
                """,
                ("uid", uid), ("tid", TenantAId));

        // The probe: a custom role that lives in tenant B.
        await Exec(conn,
            """
            INSERT INTO platform.roles (role_id, role_key, name, tenant_id, scope, is_system, created_at, updated_at)
            VALUES (@id, @key, 'GUC Tenant-B Role', @tid, 'tenant', false, NOW(), NOW())
            ON CONFLICT (role_id) DO NOTHING
            """,
            ("id", TenantBRoleId), ("key", $"guc_b_role_{TenantBRoleId.ToString("N")[..8]}"), ("tid", TenantBId));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = ANY(@u)",
            ("u", new[] { SuperAdminUserId, ControlUserId }));
        await Exec(conn, "DELETE FROM platform.roles WHERE role_id = @r", ("r", TenantBRoleId));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = ANY(@u)",
            ("u", new[] { SuperAdminUserId, ControlUserId }));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = ANY(@e)",
            ("e", new[] { SuperAdminEmail, ControlEmail }));
        foreach (var (uid, email) in new[] { (SuperAdminUserId, SuperAdminEmail), (ControlUserId, ControlEmail) })
            await Exec(conn,
                "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                ("anon", $"deleted+{uid}@guc.test"), ("u", uid));
        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = ANY(@t)",
            ("t", new[] { TenantAId, TenantBId }));
        await base.DisposeAsync();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object? Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
