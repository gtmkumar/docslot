using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Fixture for the branch / department membership-SCOPE endpoint tests (issue #90). Seeds, as the DB OWNER
/// (RLS-exempt — setup/teardown only), TWO tenants so isolation can be proven: tenant A with an <b>owner</b>
/// (system <c>tenant_owner</c> — holds tenant.users.read/update + tenant.settings.update), a <b>viewer</b>
/// (system <c>tenant_viewer</c> — read-only, lacks users.update + settings.update), a <b>noaccess</b> user
/// (an empty custom role — lacks even users.read), and a <b>target</b> member (tenant_viewer, so it has an
/// active membership to scope); plus tenant B with one pre-seeded branch (the cross-tenant negative). The app
/// under test runs as <c>docslot_app</c>, so branch reads/writes travel through RLS and set-scope through the
/// SECURITY DEFINER <c>set_membership_scope</c> — the exact production path. Teardown removes only seeded rows.
/// </summary>
public sealed class BranchScopeWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string OwnerConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string Password = "Sup3rSecret!";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid OtherTenantId { get; } = Guid.NewGuid();

    public Guid OwnerUserId { get; } = Guid.NewGuid();
    public Guid ViewerUserId { get; } = Guid.NewGuid();
    public Guid NoAccessUserId { get; } = Guid.NewGuid();
    public Guid TargetUserId { get; } = Guid.NewGuid();

    public string OwnerEmail { get; } = $"br.owner+{Guid.NewGuid():N}@docslot.test";
    public string ViewerEmail { get; } = $"br.viewer+{Guid.NewGuid():N}@docslot.test";
    public string NoAccessEmail { get; } = $"br.noaccess+{Guid.NewGuid():N}@docslot.test";
    public string TargetEmail { get; } = $"br.target+{Guid.NewGuid():N}@docslot.test";

    /// <summary>An empty, tenant-scoped custom role (zero permissions) — for the "list is gated" negative.</summary>
    public Guid EmptyRoleId { get; } = Guid.NewGuid();

    /// <summary>A branch pre-seeded under tenant B — must NEVER appear when listing tenant A.</summary>
    public Guid OtherBranchId { get; } = Guid.NewGuid();
    public string OtherBranchName { get; } = "Other-Tenant Branch (isolation)";

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var (tid, code, email) in new[]
        {
            (TenantId, "A", "brA@docslot.test"), (OtherTenantId, "B", "brB@docslot.test"),
        })
            await Exec(conn,
                """
                INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
                VALUES (@id, @code, 'Branch Scope Hospital', 'Branch Scope', 'hospital', @email, '+919744444444', 'active')
                ON CONFLICT (tenant_id) DO NOTHING
                """,
                ("id", tid), ("code", $"br-{tid.ToString()[..8]}"), ("email", email));

        foreach (var (uid, email) in new[]
        {
            (OwnerUserId, OwnerEmail), (ViewerUserId, ViewerEmail), (NoAccessUserId, NoAccessEmail), (TargetUserId, TargetEmail),
        })
            await Exec(conn,
                """
                INSERT INTO platform.users (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, created_at, updated_at)
                VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Branch Test User', true, true, false, NOW(), NOW())
                ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
                """,
                ("id", uid), ("email", email), ("pwd", Password));

        // Empty custom role (zero grants) in tenant A — the no-access principal for the list-gate test.
        await Exec(conn,
            """
            INSERT INTO platform.roles (role_id, role_key, name, tenant_id, scope, is_system, created_at, updated_at)
            VALUES (@id, @key, 'Branch Empty Role', @tid, 'tenant', false, NOW(), NOW())
            ON CONFLICT (role_id) DO NOTHING
            """,
            ("id", EmptyRoleId), ("key", $"br_empty_{EmptyRoleId.ToString("N")[..8]}"), ("tid", TenantId));

        await AssignSystemRole(conn, OwnerUserId, TenantId, "tenant_owner");
        await AssignSystemRole(conn, ViewerUserId, TenantId, "tenant_viewer");
        await AssignSystemRole(conn, TargetUserId, TenantId, "tenant_viewer");
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            VALUES (gen_random_uuid(), @uid, @tid, @rid, true, NOW())
            ON CONFLICT DO NOTHING
            """,
            ("uid", NoAccessUserId), ("tid", TenantId), ("rid", EmptyRoleId));

        // Pre-seeded branch under tenant B — the cross-tenant negative for the isolation test.
        await Exec(conn,
            """
            INSERT INTO platform.branches (branch_id, tenant_id, name, is_active)
            VALUES (@id, @tid, @name, true)
            ON CONFLICT (branch_id) DO NOTHING
            """,
            ("id", OtherBranchId), ("tid", OtherTenantId), ("name", OtherBranchName));
    }

    private static Task AssignSystemRole(NpgsqlConnection conn, Guid userId, Guid tenantId, string roleKey) =>
        Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = @key AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", userId), ("tid", tenantId), ("key", roleKey));

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        var users = new[] { OwnerUserId, ViewerUserId, NoAccessUserId, TargetUserId };
        var emails = new[] { OwnerEmail, ViewerEmail, NoAccessEmail, TargetEmail };
        // Memberships first (they FK-reference branches via branch_id), then branches, then roles/users/tenants.
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.branches WHERE tenant_id = ANY(@t)", ("t", new[] { TenantId, OtherTenantId }));
        await Exec(conn, "DELETE FROM platform.roles WHERE role_id = @rid", ("rid", EmptyRoleId));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = ANY(@e)", ("e", emails));

        foreach (var uid in users)
            await Exec(conn,
                "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                ("anon", $"deleted+{uid}@br.test"), ("u", uid));

        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = ANY(@t)",
            ("t", new[] { TenantId, OtherTenantId }));
        await base.DisposeAsync();
    }

    /// <summary>The target's scope-bearing membership (role_id + current scope) read via the RLS-exempt owner
    /// connection, using the SAME ordering the app resolves — so tests observe exactly the mutated row.</summary>
    public async Task<(Guid Utr, Guid RoleId, Guid? BranchId, string? Department)> TargetMembershipAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT user_tenant_role_id, role_id, branch_id, department
            FROM platform.user_tenant_roles
            WHERE user_id = @u AND tenant_id = @t AND revoked_at IS NULL
            ORDER BY is_primary DESC, granted_at, user_tenant_role_id
            LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue("u", TargetUserId);
        cmd.Parameters.AddWithValue("t", TenantId);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        return (
            rd.GetGuid(0),
            rd.GetGuid(1),
            await rd.IsDBNullAsync(2) ? null : rd.GetGuid(2),
            await rd.IsDBNullAsync(3) ? null : rd.GetString(3));
    }

    /// <summary>The full resolved permission set for a user in a tenant (owner connection), sorted for a stable
    /// before/after comparison — the regression assertion that scope never moves the RBAC boundary.</summary>
    public async Task<List<string>> ResolvePermissionsAsync(Guid userId, Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT permission_key FROM platform.resolve_user_permissions(@u, @t) ORDER BY 1", conn);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("t", tenantId);
        var keys = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) keys.Add(rd.GetString(0));
        return keys;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
