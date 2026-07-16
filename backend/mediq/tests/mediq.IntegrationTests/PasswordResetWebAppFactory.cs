using mediq.Application.Abstractions;
using mediq.IntegrationTests.TestDoubles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Fixture for the password-reset subsystem (self-service + admin-initiated). Seeds, as the DB OWNER
/// (RLS-exempt — setup/teardown only): a tenant with a <c>tenant_owner</c> admin, a <b>limited admin</b>
/// holding ONLY <c>tenant.users.update</c> (to prove the R3 no-escalation guard — they cannot reset a
/// higher-privileged user), a platform <b>super_admin</b> (for the platform-scope route), and several
/// <c>tenant_staff</c> targets (one per mutating test so password state never couples across tests). The app
/// under test runs as <c>docslot_app</c>, so every write travels the production path (least-privilege +
/// the SECURITY DEFINER password-reset functions). The offline notifier is swapped for a recording double so
/// tests can recover the one-time token the self-service flow never returns. Teardown soft-deletes the seeded
/// users (audit FK) and removes their tokens/sessions, leaving no residue.
/// </summary>
public sealed class PasswordResetWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string OwnerConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string Password = "Sup3rSecret!";

    public Guid TenantId { get; } = Guid.NewGuid();

    public Guid AdminUserId { get; } = Guid.NewGuid();          // tenant_owner (holds tenant.users.update)
    public Guid LimitedAdminUserId { get; } = Guid.NewGuid();   // ONLY tenant.users.update
    public Guid LimitedAdminRoleId { get; } = Guid.NewGuid();
    public Guid SuperAdminUserId { get; } = Guid.NewGuid();     // platform super_admin

    public Guid StaffSelfUserId { get; } = Guid.NewGuid();      // self-service happy path + forgot-known
    public Guid StaffTenantUserId { get; } = Guid.NewGuid();    // tenant-admin reset target
    public Guid StaffPlatformUserId { get; } = Guid.NewGuid();  // super_admin platform reset target

    public string AdminEmail { get; } = $"pr.admin+{Guid.NewGuid():N}@docslot.test";
    public string LimitedAdminEmail { get; } = $"pr.ltd+{Guid.NewGuid():N}@docslot.test";
    public string SuperAdminEmail { get; } = $"pr.super+{Guid.NewGuid():N}@docslot.test";
    public string StaffSelfEmail { get; } = $"pr.self+{Guid.NewGuid():N}@docslot.test";
    public string StaffTenantEmail { get; } = $"pr.tenant+{Guid.NewGuid():N}@docslot.test";
    public string StaffPlatformEmail { get; } = $"pr.platform+{Guid.NewGuid():N}@docslot.test";

    /// <summary>The offline notifier double — records sends and exposes the one-time token.</summary>
    public RecordingPasswordResetNotifier Notifier { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPasswordResetNotifier>();
            services.AddSingleton<IPasswordResetNotifier>(Notifier);
        });
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (@id, @code, 'Password Reset Hospital', 'PwReset', 'hospital', 'pr@docslot.test', '+919744444444', 'active')
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            ("id", TenantId), ("code", $"pr-{TenantId.ToString()[..8]}"));

        foreach (var (uid, email, platform) in new[]
        {
            (AdminUserId, AdminEmail, false),
            (LimitedAdminUserId, LimitedAdminEmail, false),
            (SuperAdminUserId, SuperAdminEmail, true),
            (StaffSelfUserId, StaffSelfEmail, false),
            (StaffTenantUserId, StaffTenantEmail, false),
            (StaffPlatformUserId, StaffPlatformEmail, false),
        })
            await Exec(conn,
                """
                INSERT INTO platform.users (user_id, email, phone, password_hash, full_name, email_verified, is_active, is_platform_user, created_at, updated_at)
                VALUES (@id, @email, '+919700000002', crypt(@pwd, gen_salt('bf', 10)), 'PwReset Test User', true, true, @plat, NOW(), NOW())
                ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
                """,
                ("id", uid), ("email", email), ("pwd", Password), ("plat", platform));

        // Admin = tenant_owner (holds tenant.users.update + the full owner permission set → outranks LimitedAdmin).
        await AssignSystemRole(conn, AdminUserId, TenantId, "tenant_owner");

        // Staff targets = tenant_staff.
        foreach (var sid in new[] { StaffSelfUserId, StaffTenantUserId, StaffPlatformUserId })
            await AssignSystemRole(conn, sid, TenantId, "tenant_staff");

        // Super admin: platform super_admin (tenant_id NULL) + a tenant_owner membership so login-with-tenant works.
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, NULL, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = 'super_admin' AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", SuperAdminUserId));
        await AssignSystemRole(conn, SuperAdminUserId, TenantId, "tenant_owner");

        // Limited admin: a custom role granting ONLY tenant.users.update (can reset, but not a full admin).
        await Exec(conn,
            """
            INSERT INTO platform.roles (role_id, role_key, name, tenant_id, scope, is_system, created_at, updated_at)
            VALUES (@id, @key, 'Limited Reset Admin', @tid, 'tenant', false, NOW(), NOW())
            ON CONFLICT (role_id) DO NOTHING
            """,
            ("id", LimitedAdminRoleId), ("key", $"pr_ltd_{LimitedAdminRoleId.ToString("N")[..8]}"), ("tid", TenantId));
        await Exec(conn,
            """
            INSERT INTO platform.role_permissions (role_id, permission_id, is_grantable, granted_at)
            SELECT @rid, p.permission_id, false, NOW()
            FROM platform.permissions p WHERE p.permission_key = 'tenant.users.update'
            ON CONFLICT DO NOTHING
            """,
            ("rid", LimitedAdminRoleId));
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            VALUES (gen_random_uuid(), @uid, @tid, @rid, true, NOW())
            ON CONFLICT DO NOTHING
            """,
            ("uid", LimitedAdminUserId), ("tid", TenantId), ("rid", LimitedAdminRoleId));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        var users = new[]
        {
            AdminUserId, LimitedAdminUserId, SuperAdminUserId,
            StaffSelfUserId, StaffTenantUserId, StaffPlatformUserId,
        };

        await Exec(conn, "DELETE FROM platform.password_reset_tokens WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.role_permissions WHERE role_id = @rid", ("rid", LimitedAdminRoleId));
        await Exec(conn, "DELETE FROM platform.roles WHERE role_id = @rid", ("rid", LimitedAdminRoleId));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = ANY(@e)",
            ("e", new[] { AdminEmail, LimitedAdminEmail, SuperAdminEmail, StaffSelfEmail, StaffTenantEmail, StaffPlatformEmail }));

        foreach (var uid in users)
            await Exec(conn,
                "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                ("anon", $"deleted+{uid}@pr.test"), ("u", uid));

        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = @t",
            ("t", TenantId));
        await base.DisposeAsync();
    }

    // ---- assertion helpers (RLS-exempt owner connection) ---------------------------------------------

    /// <summary>Count of password-reset token rows for a user (optionally only the unused, unexpired ones).</summary>
    public static async Task<int> TokenRowCountAsync(Guid userId, bool liveOnly)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        var sql = liveOnly
            ? "SELECT COUNT(*) FROM platform.password_reset_tokens WHERE user_id = @u AND used_at IS NULL AND expires_at > NOW()"
            : "SELECT COUNT(*) FROM platform.password_reset_tokens WHERE user_id = @u";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("u", userId);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Total row count in password_reset_tokens (for the unknown-email "nothing minted" probe).</summary>
    public static async Task<long> TotalTokenRowsAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM platform.password_reset_tokens", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Number of ACTIVE (non-revoked) sessions for a user (proves consume revoked them).</summary>
    public static async Task<int> ActiveSessionCountAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM platform.user_sessions WHERE user_id = @u AND revoked_at IS NULL", conn);
        cmd.Parameters.AddWithValue("u", userId);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>True if the user has a session revoked with reason 'password_reset' (proves the revoke reason).</summary>
    public static async Task<bool> HasPasswordResetRevokedSessionAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM platform.user_sessions WHERE user_id = @u AND revoked_reason = 'password_reset')", conn);
        cmd.Parameters.AddWithValue("u", userId);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>True if a Success=false 'Password reset denied' audit row exists for this (target, actor) pair.
    /// Both ids are fresh per fixture run, so the match is run-specific (audit rows are append-only, never swept).</summary>
    public static async Task<bool> HasDeniedResetAuditAsync(Guid targetUserId, Guid actorUserId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT EXISTS(
                SELECT 1 FROM platform.audit_log
                WHERE resource_type = 'user' AND resource_id = @t AND user_id = @a
                  AND action = 'update' AND success = false AND change_summary = 'Password reset denied')
            """, conn);
        cmd.Parameters.AddWithValue("t", targetUserId);
        cmd.Parameters.AddWithValue("a", actorUserId);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Deletes any existing tokens for a user so a test starts from a clean token state.</summary>
    public static async Task ClearTokensAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM platform.password_reset_tokens WHERE user_id = @u", conn);
        cmd.Parameters.AddWithValue("u", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Forces every live token for a user into the past (simulates an aged reset link).</summary>
    public static async Task ExpireTokensAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE platform.password_reset_tokens SET expires_at = NOW() - interval '1 hour' WHERE user_id = @u AND used_at IS NULL", conn);
        cmd.Parameters.AddWithValue("u", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task AssignSystemRole(NpgsqlConnection conn, Guid userId, Guid tenantId, string roleKey) =>
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = @key AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", userId), ("tid", tenantId), ("key", roleKey));

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
