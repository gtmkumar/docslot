using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Fixture for the People export + bulk-import tests (issue #95, Phase D of epic #80). Seeds, as the DB OWNER
/// (RLS-exempt — setup/teardown only): tenant A with an <b>owner</b> (system <c>tenant_owner</c> — holds
/// tenant.users.read + tenant.users.create + tenant.roles.assign WITH grant option), a <b>no-access</b> member
/// (an EMPTY custom role — lacks users.read + users.create, so it proves both gates 403), and an
/// <b>injection</b> member whose full_name starts with <c>=</c> (proves the export neutralises formula
/// injection); a custom <b>conferrable</b> tenant role holding only <c>docslot.booking.read</c> (a perm the
/// owner may confer); and tenant B with its own owner member (proves the export is tenant-scoped). The app
/// under test runs as <c>docslot_app</c>, so every write travels the production path (RLS + the SECURITY
/// DEFINER functions). Teardown removes the seeded rows plus any users the import tests mint (swept by email
/// prefix), leaving no residue.
/// </summary>
public sealed class PeopleImportExportWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string OwnerConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string Password = "Sup3rSecret!";

    /// <summary>A tenant perm the owner holds WITH grant option — the sole perm on the conferrable role.</summary>
    public const string ConferrablePermissionKey = "docslot.booking.read";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid TenantBId { get; } = Guid.NewGuid();

    public Guid OwnerUserId { get; } = Guid.NewGuid();
    public Guid NoAccessUserId { get; } = Guid.NewGuid();
    public Guid InjectionUserId { get; } = Guid.NewGuid();
    public Guid TenantBOwnerUserId { get; } = Guid.NewGuid();

    /// <summary>An empty custom role (no permissions) — its holder passes login but resolves to zero perms.</summary>
    public Guid NoAccessRoleId { get; } = Guid.NewGuid();

    /// <summary>A custom tenant role holding only <see cref="ConferrablePermissionKey"/> — the owner may confer it.</summary>
    public Guid ConferRoleId { get; } = Guid.NewGuid();
    public string ConferRoleKey { get; } = $"p95_confer_{Guid.NewGuid():N}"[..18];

    public string OwnerEmail { get; } = $"p95.owner+{Guid.NewGuid():N}@docslot.test";
    public string NoAccessEmail { get; } = $"p95.noaccess+{Guid.NewGuid():N}@docslot.test";
    public string InjectionEmail { get; } = $"p95.inject+{Guid.NewGuid():N}@docslot.test";
    public string TenantBOwnerEmail { get; } = $"p95.tenantb+{Guid.NewGuid():N}@docslot.test";

    /// <summary>The formula-injection payload seeded as a member's full_name (must be neutralised on export).</summary>
    public string InjectionFullName { get; } = "=SUM(A1:A2)";

    /// <summary>Prefix for users the bulk-import tests mint, so teardown sweeps them by email.</summary>
    public string ImportPrefix { get; } = $"p95.imported+{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var (tid, code, name) in new[]
        {
            (TenantId, $"p95a-{TenantId.ToString()[..8]}", "People95 A"),
            (TenantBId, $"p95b-{TenantBId.ToString()[..8]}", "People95 B"),
        })
            await Exec(conn,
                """
                INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
                VALUES (@id, @code, @name, @name, 'hospital', @code||'@p95.test', '+919744444444', 'active')
                ON CONFLICT (tenant_id) DO NOTHING
                """,
                ("id", tid), ("code", code), ("name", name));

        foreach (var (uid, email, fullName) in new[]
        {
            (OwnerUserId, OwnerEmail, "People95 Owner"),
            (NoAccessUserId, NoAccessEmail, "People95 NoAccess"),
            (InjectionUserId, InjectionEmail, InjectionFullName),
            (TenantBOwnerUserId, TenantBOwnerEmail, "People95 TenantB Owner"),
        })
            await Exec(conn,
                """
                INSERT INTO platform.users (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, created_at, updated_at)
                VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), @fn, true, true, false, NOW(), NOW())
                ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
                """,
                ("id", uid), ("email", email), ("pwd", Password), ("fn", fullName));

        // Owner: tenant_owner in tenant A. TenantB owner: tenant_owner in tenant B.
        await AssignSystemRole(conn, OwnerUserId, TenantId, "tenant_owner");
        await AssignSystemRole(conn, TenantBOwnerUserId, TenantBId, "tenant_owner");

        // Empty custom role (no perms) in tenant A — the no-access + injection members hold it (they are members
        // of tenant A so they appear in the export, but the no-access member resolves to zero permissions).
        await Exec(conn,
            """
            INSERT INTO platform.roles (role_id, role_key, name, tenant_id, scope, is_system, created_at, updated_at)
            VALUES (@id, @key, 'People95 No-Access Role', @tid, 'tenant', false, NOW(), NOW())
            ON CONFLICT (role_id) DO NOTHING
            """,
            ("id", NoAccessRoleId), ("key", $"p95_noaccess_{NoAccessRoleId.ToString("N")[..8]}"), ("tid", TenantId));
        await AssignCustomRole(conn, NoAccessUserId, TenantId, NoAccessRoleId);
        await AssignCustomRole(conn, InjectionUserId, TenantId, NoAccessRoleId);

        // Conferrable custom role in tenant A: only docslot.booking.read (owner holds it WITH grant option).
        await Exec(conn,
            """
            INSERT INTO platform.roles (role_id, role_key, name, tenant_id, scope, is_system, created_at, updated_at)
            VALUES (@id, @key, 'People95 Conferrable Role', @tid, 'tenant', false, NOW(), NOW())
            ON CONFLICT (role_id) DO NOTHING
            """,
            ("id", ConferRoleId), ("key", ConferRoleKey), ("tid", TenantId));
        await Exec(conn,
            """
            INSERT INTO platform.role_permissions (role_id, permission_id, is_grantable, granted_at)
            SELECT @rid, p.permission_id, false, NOW()
            FROM platform.permissions p WHERE p.permission_key = @perm
            ON CONFLICT DO NOTHING
            """,
            ("rid", ConferRoleId), ("perm", ConferrablePermissionKey));
    }

    private static Task AssignSystemRole(NpgsqlConnection conn, Guid userId, Guid tenantId, string roleKey) =>
        Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = @rk AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", userId), ("tid", tenantId), ("rk", roleKey));

    private static Task AssignCustomRole(NpgsqlConnection conn, Guid userId, Guid tenantId, Guid roleId) =>
        Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            VALUES (gen_random_uuid(), @uid, @tid, @rid, true, NOW())
            ON CONFLICT DO NOTHING
            """,
            ("uid", userId), ("tid", tenantId), ("rid", roleId));

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        // Users minted by the import tests (+ their memberships + any assignments they got), swept by prefix.
        await Exec(conn,
            "DELETE FROM platform.user_tenant_roles WHERE user_id IN (SELECT user_id FROM platform.users WHERE email LIKE @p)",
            ("p", ImportPrefix + "%"));
        await Exec(conn, "DELETE FROM platform.users WHERE email LIKE @p", ("p", ImportPrefix + "%"));

        var users = new[] { OwnerUserId, NoAccessUserId, InjectionUserId, TenantBOwnerUserId };
        var emails = new[] { OwnerEmail, NoAccessEmail, InjectionEmail, TenantBOwnerEmail };
        var roles = new[] { NoAccessRoleId, ConferRoleId };
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.role_permissions WHERE role_id = ANY(@r)", ("r", roles));
        await Exec(conn, "DELETE FROM platform.roles WHERE role_id = ANY(@r)", ("r", roles));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = ANY(@e)", ("e", emails));

        foreach (var uid in users)
            await Exec(conn,
                "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                ("anon", $"deleted+{uid}@p95.test"), ("u", uid));

        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = ANY(@t)",
            ("t", new[] { TenantId, TenantBId }));
        await base.DisposeAsync();
    }

    /// <summary>True if a live (not soft-deleted) user with this email exists (proves create rollback / linking).</summary>
    public static async Task<bool> LiveUserExistsAsync(string email)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM platform.users WHERE email = @e AND deleted_at IS NULL)", conn);
        cmd.Parameters.AddWithValue("e", email);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Counts ACTIVE assignments of a role to a user in a tenant (proves a bulk row conferred a role).</summary>
    public static async Task<int> ActiveRoleAssignmentCountAsync(string email, Guid roleId, Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COUNT(*)::int
            FROM platform.user_tenant_roles utr
            JOIN platform.users u ON u.user_id = utr.user_id
            WHERE u.email = @e AND utr.role_id = @r AND utr.tenant_id = @t AND utr.revoked_at IS NULL
            """, conn);
        cmd.Parameters.AddWithValue("e", email);
        cmd.Parameters.AddWithValue("r", roleId);
        cmd.Parameters.AddWithValue("t", tenantId);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
