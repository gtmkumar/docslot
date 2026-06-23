using mediq.Application.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Fixture for the RBAC-hardening write-path tests (database/11_rbac_hardening.sql). It seeds, as the DB
/// OWNER (<c>gtmkumar</c>, RLS-exempt — setup/teardown only), a tenant plus three real users:
/// <list type="bullet">
///   <item>an <b>owner</b> with the system <c>tenant_owner</c> role (holds tenant.roles.assign +
///   platform.overrides.grant) — the privileged actor,</item>
///   <item>a <b>viewer</b> with the system <c>tenant_viewer</c> role (holds neither) — the under-privileged
///   actor used to prove the escalation guard returns 403,</item>
///   <item>a <b>target</b> user that receives assignments/overrides.</item>
/// </list>
/// Two empty custom roles are created for SoD pairing. The app under test runs as <c>docslot_app</c>
/// (appsettings "platform-db"), so every write travels through RLS + the SECURITY DEFINER functions — the
/// exact production path. Teardown removes only the seeded rows; the schema is never mutated.
/// </summary>
public sealed class RbacHardeningWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // OWNER connection — RLS-exempt — for deterministic seeding/cleanup only.
    public const string OwnerConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string Password = "Sup3rSecret!";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid OwnerUserId { get; } = Guid.NewGuid();
    public Guid ViewerUserId { get; } = Guid.NewGuid();
    public Guid TargetUserId { get; } = Guid.NewGuid();

    /// <summary>Two empty, tenant-scoped custom roles — assigning both to one user trips the SoD trigger.</summary>
    public Guid SodRoleAId { get; } = Guid.NewGuid();
    public Guid SodRoleBId { get; } = Guid.NewGuid();

    /// <summary>A spare empty custom role for the happy-path assignment (no permissions ⇒ no escalation).</summary>
    public Guid PlainRoleId { get; } = Guid.NewGuid();

    public string OwnerEmail { get; } = $"rbac.owner+{Guid.NewGuid():N}@docslot.test";
    public string ViewerEmail { get; } = $"rbac.viewer+{Guid.NewGuid():N}@docslot.test";
    public string TargetEmail { get; } = $"rbac.target+{Guid.NewGuid():N}@docslot.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (@id, @code, 'RBAC Hardening Hospital', 'RBAC Hardening', 'hospital', 'rbac@docslot.test', '+919777777777', 'active')
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            ("id", TenantId), ("code", $"rbac-{TenantId.ToString()[..8]}"));

        foreach (var (uid, email) in new[] { (OwnerUserId, OwnerEmail), (ViewerUserId, ViewerEmail), (TargetUserId, TargetEmail) })
            await Exec(conn,
                """
                INSERT INTO platform.users (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, created_at, updated_at)
                VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'RBAC Test User', true, true, false, NOW(), NOW())
                ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
                """,
                ("id", uid), ("email", email), ("pwd", Password));

        // Owner gets tenant_owner; viewer gets tenant_viewer — both IN the test tenant.
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

        // Three EMPTY custom tenant-scoped roles (no role_permissions ⇒ never trip the escalation guard).
        foreach (var (rid, key) in new[]
        {
            (PlainRoleId, $"rbac_plain_{PlainRoleId.ToString("N")[..8]}"),
            (SodRoleAId,  $"rbac_sod_a_{SodRoleAId.ToString("N")[..8]}"),
            (SodRoleBId,  $"rbac_sod_b_{SodRoleBId.ToString("N")[..8]}"),
        })
            await Exec(conn,
                """
                INSERT INTO platform.roles (role_id, role_key, name, tenant_id, scope, is_system, created_at, updated_at)
                VALUES (@id, @key, 'RBAC Custom Role', @tid, 'tenant', false, NOW(), NOW())
                ON CONFLICT (role_id) DO NOTHING
                """,
                ("id", rid), ("key", key), ("tid", TenantId));

        // Declare the SoD incompatibility pair (stored once; the trigger checks both orderings).
        await Exec(conn,
            """
            INSERT INTO platform.role_incompatibility (role_a_id, role_b_id, reason)
            VALUES (@a, @b, 'RBAC hardening test: SoD pair')
            ON CONFLICT (role_a_id, role_b_id) DO NOTHING
            """,
            ("a", SodRoleAId), ("b", SodRoleBId));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await Exec(conn, "DELETE FROM platform.role_incompatibility WHERE role_a_id = @a OR role_b_id = @a", ("a", SodRoleAId));
        await Exec(conn, "DELETE FROM platform.user_permission_overrides WHERE user_id = ANY(@u)",
            ("u", new[] { OwnerUserId, ViewerUserId, TargetUserId }));
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = ANY(@u)",
            ("u", new[] { OwnerUserId, ViewerUserId, TargetUserId }));
        await Exec(conn, "DELETE FROM platform.roles WHERE role_id = ANY(@r)",
            ("r", new[] { PlainRoleId, SodRoleAId, SodRoleBId }));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = ANY(@u)",
            ("u", new[] { OwnerUserId, ViewerUserId, TargetUserId }));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = ANY(@e)",
            ("e", new[] { OwnerEmail, ViewerEmail, TargetEmail }));

        foreach (var (uid, email) in new[] { (OwnerUserId, OwnerEmail), (ViewerUserId, ViewerEmail), (TargetUserId, TargetEmail) })
            await Exec(conn,
                "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                ("anon", $"deleted+{uid}@rbac.test"), ("u", uid));

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
