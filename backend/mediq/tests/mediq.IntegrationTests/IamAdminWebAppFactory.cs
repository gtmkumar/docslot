using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Fixture for the IAM (Roles &amp; permissions admin) endpoint tests. Seeds, as the DB OWNER
/// (RLS-exempt — setup/teardown only), a tenant plus an <b>owner</b> (system <c>tenant_owner</c> — holds
/// tenant.users.read + tenant.roles.assign, and every tenant/self permission WITH grant option), a
/// <b>viewer</b> (system <c>tenant_viewer</c> — read-only, lacks tenant.roles.assign), a <b>target</b>
/// user, and one EMPTY custom tenant-scoped role to toggle permissions on. The app under test runs as
/// <c>docslot_app</c>, so every write travels through RLS + the SECURITY DEFINER functions
/// (grant/revoke_permission_from_role, duplicate_role) — the exact production path. Teardown removes only
/// the seeded rows (including any roles the duplicate tests mint), so the suite leaves no residue.
/// </summary>
public sealed class IamAdminWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string OwnerConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string Password = "Sup3rSecret!";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid OwnerUserId { get; } = Guid.NewGuid();
    public Guid ViewerUserId { get; } = Guid.NewGuid();
    public Guid TargetUserId { get; } = Guid.NewGuid();

    /// <summary>An empty, tenant-scoped custom role — the editable target for grant/revoke toggle tests.</summary>
    public Guid CustomRoleId { get; } = Guid.NewGuid();

    /// <summary>Marks the duplicate-minted roles so teardown can sweep them regardless of generated id.</summary>
    public string DuplicateKeyPrefix { get; } = $"iam_dup_{Guid.NewGuid():N}"[..16];

    public string OwnerEmail { get; } = $"iam.owner+{Guid.NewGuid():N}@docslot.test";
    public string ViewerEmail { get; } = $"iam.viewer+{Guid.NewGuid():N}@docslot.test";
    public string TargetEmail { get; } = $"iam.target+{Guid.NewGuid():N}@docslot.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (@id, @code, 'IAM Admin Hospital', 'IAM Admin', 'hospital', 'iam@docslot.test', '+919766666666', 'active')
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            ("id", TenantId), ("code", $"iam-{TenantId.ToString()[..8]}"));

        foreach (var (uid, email) in new[] { (OwnerUserId, OwnerEmail), (ViewerUserId, ViewerEmail), (TargetUserId, TargetEmail) })
            await Exec(conn,
                """
                INSERT INTO platform.users (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, created_at, updated_at)
                VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'IAM Test User', true, true, false, NOW(), NOW())
                ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
                """,
                ("id", uid), ("email", email), ("pwd", Password));

        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = 'tenant_owner' AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", OwnerUserId), ("tid", TenantId));

        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = 'tenant_viewer' AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", ViewerUserId), ("tid", TenantId));

        // One EMPTY custom tenant-scoped role: the editable target for the matrix toggle tests.
        await Exec(conn,
            """
            INSERT INTO platform.roles (role_id, role_key, name, tenant_id, scope, is_system, created_at, updated_at)
            VALUES (@id, @key, 'IAM Editable Role', @tid, 'tenant', false, NOW(), NOW())
            ON CONFLICT (role_id) DO NOTHING
            """,
            ("id", CustomRoleId), ("key", $"iam_custom_{CustomRoleId.ToString("N")[..8]}"), ("tid", TenantId));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        // Grants on the custom role + on any duplicate-minted roles, then the roles themselves.
        await Exec(conn,
            """
            DELETE FROM platform.role_permissions
            WHERE role_id = @cr
               OR role_id IN (SELECT role_id FROM platform.roles WHERE tenant_id = @tid AND role_key LIKE @dup)
            """,
            ("cr", CustomRoleId), ("tid", TenantId), ("dup", DuplicateKeyPrefix + "%"));
        await Exec(conn, "DELETE FROM platform.user_permission_overrides WHERE user_id = ANY(@u)",
            ("u", new[] { OwnerUserId, ViewerUserId, TargetUserId }));
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = ANY(@u)",
            ("u", new[] { OwnerUserId, ViewerUserId, TargetUserId }));
        await Exec(conn, "DELETE FROM platform.roles WHERE role_id = @cr OR (tenant_id = @tid AND role_key LIKE @dup)",
            ("cr", CustomRoleId), ("tid", TenantId), ("dup", DuplicateKeyPrefix + "%"));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = ANY(@u)",
            ("u", new[] { OwnerUserId, ViewerUserId, TargetUserId }));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = ANY(@e)",
            ("e", new[] { OwnerEmail, ViewerEmail, TargetEmail }));

        foreach (var (uid, _) in new[] { (OwnerUserId, OwnerEmail), (ViewerUserId, ViewerEmail), (TargetUserId, TargetEmail) })
            await Exec(conn,
                "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                ("anon", $"deleted+{uid}@iam.test"), ("u", uid));

        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = @t", ("t", TenantId));
        await base.DisposeAsync();
    }

    /// <summary>Resolves a permission_id by key via the RLS-exempt owner connection (test arrangement only).</summary>
    public static async Task<Guid> PermissionIdAsync(string permissionKey)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT permission_id FROM platform.permissions WHERE permission_key = @k", conn);
        cmd.Parameters.AddWithValue("k", permissionKey);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Resolves a system role_id by key via the RLS-exempt owner connection (test arrangement only).</summary>
    public static async Task<Guid> SystemRoleIdAsync(string roleKey)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT role_id FROM platform.roles WHERE role_key = @k AND is_system = true", conn);
        cmd.Parameters.AddWithValue("k", roleKey);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>True if the role grants the permission (read via the RLS-exempt owner connection).</summary>
    public static async Task<bool> RoleHasPermissionAsync(Guid roleId, Guid permissionId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM platform.role_permissions WHERE role_id = @r AND permission_id = @p)", conn);
        cmd.Parameters.AddWithValue("r", roleId);
        cmd.Parameters.AddWithValue("p", permissionId);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Counts grants on a role split by grant-option flag — proves duplicate forces is_grantable=false.</summary>
    public static async Task<(int Grantable, int NonGrantable)> GrantOptionSplitAsync(Guid roleId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT
              count(*) FILTER (WHERE is_grantable),
              count(*) FILTER (WHERE NOT is_grantable)
            FROM platform.role_permissions WHERE role_id = @r
            """, conn);
        cmd.Parameters.AddWithValue("r", roleId);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        return ((int)(long)rd.GetInt64(0), (int)(long)rd.GetInt64(1));
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
