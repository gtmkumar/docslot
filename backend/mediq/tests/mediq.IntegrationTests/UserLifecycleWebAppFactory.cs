using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Fixture for the user-lifecycle endpoint tests (invite / deactivate / reactivate / edit profile /
/// reset access). Seeds, as the DB OWNER (RLS-exempt — setup/teardown only): a tenant; two admins
/// (system <c>tenant_owner</c>) so deactivating one admin leaves another; a plain <b>member</b>
/// (system <c>tenant_staff</c>) as the lifecycle target; and a <b>limited inviter</b> holding a custom
/// role with ONLY <c>tenant.users.create</c> — used to prove the escalation-by-proxy fix (they can invite
/// but cannot confer a role). The app under test runs as <c>docslot_app</c>, so every write travels the
/// production path (RLS + the SECURITY DEFINER lifecycle functions). Teardown removes only the seeded rows
/// plus any users the invite tests mint (swept by email prefix), leaving no residue.
/// </summary>
public sealed class UserLifecycleWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string OwnerConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string Password = "Sup3rSecret!";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid AdminUserId { get; } = Guid.NewGuid();
    public Guid Admin2UserId { get; } = Guid.NewGuid();
    public Guid MemberUserId { get; } = Guid.NewGuid();
    public Guid InviterUserId { get; } = Guid.NewGuid();
    public Guid InviterRoleId { get; } = Guid.NewGuid();

    public string AdminEmail { get; } = $"ul.admin+{Guid.NewGuid():N}@docslot.test";
    public string Admin2Email { get; } = $"ul.admin2+{Guid.NewGuid():N}@docslot.test";
    public string MemberEmail { get; } = $"ul.member+{Guid.NewGuid():N}@docslot.test";
    public string InviterEmail { get; } = $"ul.inviter+{Guid.NewGuid():N}@docslot.test";

    /// <summary>Prefix for users minted by invite tests, so teardown sweeps them by email.</summary>
    public string InvitePrefix { get; } = $"ul.invited+{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (@id, @code, 'Lifecycle Hospital', 'Lifecycle', 'hospital', 'ul@docslot.test', '+919755555555', 'active')
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            ("id", TenantId), ("code", $"ul-{TenantId.ToString()[..8]}"));

        foreach (var (uid, email) in new[]
        {
            (AdminUserId, AdminEmail), (Admin2UserId, Admin2Email), (MemberUserId, MemberEmail), (InviterUserId, InviterEmail),
        })
            await Exec(conn,
                """
                INSERT INTO platform.users (user_id, email, phone, password_hash, full_name, email_verified, is_active, created_at, updated_at)
                VALUES (@id, @email, '+919700000001', crypt(@pwd, gen_salt('bf', 10)), 'Lifecycle Test User', true, true, NOW(), NOW())
                ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
                """,
                ("id", uid), ("email", email), ("pwd", Password));

        // Two tenant_owner admins + one tenant_staff member.
        foreach (var (uid, roleKey) in new[]
        {
            (AdminUserId, "tenant_owner"), (Admin2UserId, "tenant_owner"), (MemberUserId, "tenant_staff"),
        })
            await Exec(conn,
                """
                INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
                SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
                FROM platform.roles r WHERE r.role_key = @rk AND r.is_system = true
                ON CONFLICT DO NOTHING
                """,
                ("uid", uid), ("tid", TenantId), ("rk", roleKey));

        // A custom tenant role granting ONLY tenant.users.create, assigned to the limited inviter.
        await Exec(conn,
            """
            INSERT INTO platform.roles (role_id, role_key, name, tenant_id, scope, is_system, created_at, updated_at)
            VALUES (@id, @key, 'Limited Inviter', @tid, 'tenant', false, NOW(), NOW())
            ON CONFLICT (role_id) DO NOTHING
            """,
            ("id", InviterRoleId), ("key", $"ul_inviter_{InviterRoleId.ToString("N")[..8]}"), ("tid", TenantId));
        await Exec(conn,
            """
            INSERT INTO platform.role_permissions (role_id, permission_id, is_grantable, granted_at)
            SELECT @rid, p.permission_id, false, NOW()
            FROM platform.permissions p WHERE p.permission_key = 'tenant.users.create'
            ON CONFLICT DO NOTHING
            """,
            ("rid", InviterRoleId));
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            VALUES (gen_random_uuid(), @uid, @tid, @rid, true, NOW())
            ON CONFLICT DO NOTHING
            """,
            ("uid", InviterUserId), ("tid", TenantId), ("rid", InviterRoleId));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        // Users minted by the invite tests (+ their memberships), swept by email prefix.
        await Exec(conn,
            "DELETE FROM platform.user_tenant_roles WHERE user_id IN (SELECT user_id FROM platform.users WHERE email LIKE @p)",
            ("p", InvitePrefix + "%"));
        await Exec(conn, "DELETE FROM platform.users WHERE email LIKE @p", ("p", InvitePrefix + "%"));

        var users = new[] { AdminUserId, Admin2UserId, MemberUserId, InviterUserId };
        var emails = new[] { AdminEmail, Admin2Email, MemberEmail, InviterEmail };
        await Exec(conn, "DELETE FROM platform.user_permission_overrides WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.role_permissions WHERE role_id = @rid", ("rid", InviterRoleId));
        await Exec(conn, "DELETE FROM platform.roles WHERE role_id = @rid", ("rid", InviterRoleId));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = ANY(@e)", ("e", emails));

        foreach (var uid in users)
            await Exec(conn,
                "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                ("anon", $"deleted+{uid}@ul.test"), ("u", uid));

        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = @t", ("t", TenantId));
        await base.DisposeAsync();
    }

    /// <summary>Reads a user column via the RLS-exempt owner connection (test assertion only).</summary>
    public static async Task<object?> UserScalarAsync(Guid userId, string column)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT {column} FROM platform.users WHERE user_id = @u", conn);
        cmd.Parameters.AddWithValue("u", userId);
        return await cmd.ExecuteScalarAsync();
    }

    /// <summary>True if a user with this email exists and is not soft-deleted (proves create rollback).</summary>
    public static async Task<bool> LiveUserExistsAsync(string email)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM platform.users WHERE email = @e AND deleted_at IS NULL)", conn);
        cmd.Parameters.AddWithValue("e", email);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
