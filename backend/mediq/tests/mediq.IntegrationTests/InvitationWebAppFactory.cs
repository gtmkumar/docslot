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
/// Fixture for the invitations subsystem (issue #89). Seeds, as the DB OWNER (RLS-exempt — setup/teardown
/// only): a primary tenant with a <c>tenant_owner</c> admin and a <b>limited inviter</b> holding a custom role
/// with ONLY <c>tenant.users.create</c> (to prove the R3 no-escalation guard — they can invite but cannot
/// confer a role); plus a SECOND tenant carrying a pre-seeded pending invitation (to prove tenant isolation
/// of the list). The app under test runs as <c>docslot_app</c>, so every write travels the production path
/// (RLS + the SECURITY DEFINER invitation functions). Teardown removes all invitations for both tenants and
/// any users minted by accept tests (swept by email prefix), leaving no residue.
/// </summary>
public sealed class InvitationWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string OwnerConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string Password = "Sup3rSecret!";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid OtherTenantId { get; } = Guid.NewGuid();
    public Guid AdminUserId { get; } = Guid.NewGuid();
    public Guid InviterUserId { get; } = Guid.NewGuid();
    public Guid InviterRoleId { get; } = Guid.NewGuid();

    /// <summary>A pending invitation seeded in the OTHER tenant — must never appear in this tenant's list.</summary>
    public Guid OtherTenantInvitationId { get; } = Guid.NewGuid();

    public string AdminEmail { get; } = $"inv.admin+{Guid.NewGuid():N}@docslot.test";
    public string InviterEmail { get; } = $"inv.inviter+{Guid.NewGuid():N}@docslot.test";

    /// <summary>Prefix for emails invited/accepted by tests, so teardown sweeps the minted users + invites.</summary>
    public string InvitePrefix { get; } = $"inv.invited+{Guid.NewGuid():N}";

    /// <summary>The offline #93 notifier double swapped in for the stub — records sends / can be armed to throw.</summary>
    public RecordingInvitationNotifier Notifier { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Replace the offline StubInvitationNotifier with a recording double so tests can assert the advisory
        // dispatch fired (and can force it to throw to prove it never fails the invite).
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IInvitationNotifier>();
            services.AddSingleton<IInvitationNotifier>(Notifier);
        });
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var (tid, code, email) in new[]
        {
            (TenantId, $"inv-{TenantId.ToString()[..8]}", "inv-a@docslot.test"),
            (OtherTenantId, $"inv-{OtherTenantId.ToString()[..8]}", "inv-b@docslot.test"),
        })
            await Exec(conn,
                """
                INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
                VALUES (@id, @code, 'Invite Hospital', 'Invite', 'hospital', @email, '+919755555555', 'active')
                ON CONFLICT (tenant_id) DO NOTHING
                """,
                ("id", tid), ("code", code), ("email", email));

        foreach (var (uid, email) in new[] { (AdminUserId, AdminEmail), (InviterUserId, InviterEmail) })
            await Exec(conn,
                """
                INSERT INTO platform.users (user_id, email, phone, password_hash, full_name, email_verified, is_active, created_at, updated_at)
                VALUES (@id, @email, '+919700000001', crypt(@pwd, gen_salt('bf', 10)), 'Invite Test User', true, true, NOW(), NOW())
                ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
                """,
                ("id", uid), ("email", email), ("pwd", Password));

        // Admin = tenant_owner (holds tenant.users.create + read + roles.assign, is another admin).
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = 'tenant_owner' AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", AdminUserId), ("tid", TenantId));

        // Limited inviter: a custom role granting ONLY tenant.users.create (no read, no roles.assign).
        await Exec(conn,
            """
            INSERT INTO platform.roles (role_id, role_key, name, tenant_id, scope, is_system, created_at, updated_at)
            VALUES (@id, @key, 'Limited Inviter', @tid, 'tenant', false, NOW(), NOW())
            ON CONFLICT (role_id) DO NOTHING
            """,
            ("id", InviterRoleId), ("key", $"inv_inviter_{InviterRoleId.ToString("N")[..8]}"), ("tid", TenantId));
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

        // A pending invitation in the OTHER tenant (isolation probe). token_hash is opaque — never redeemed.
        await Exec(conn,
            """
            INSERT INTO platform.invitations
                (invitation_id, tenant_id, invited_email, token_hash, status, expires_at, resend_count, created_at, updated_at)
            VALUES (@id, @tid, @email, @hash, 'pending', NOW() + interval '7 days', 0, NOW(), NOW())
            ON CONFLICT (invitation_id) DO NOTHING
            """,
            ("id", OtherTenantInvitationId), ("tid", OtherTenantId),
            ("email", $"{InvitePrefix}.other-tenant@docslot.test"), ("hash", $"seed-hash-{Guid.NewGuid():N}"));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        // Invitations for both tenants (seeded + any minted by tests).
        await Exec(conn, "DELETE FROM platform.invitations WHERE tenant_id = ANY(@t)",
            ("t", new[] { TenantId, OtherTenantId }));

        // Users minted by accept tests (+ their memberships), swept by email prefix. These are SOFT-deleted
        // (not hard-deleted): an accepted user logs in during the happy-path test, which appends a login row to
        // the append-only audit_log (FK to users) — a hard DELETE would trip that FK. Anonymise the email so a
        // re-run's fresh prefix never collides on the UNIQUE(email) index.
        await Exec(conn,
            "DELETE FROM platform.user_tenant_roles WHERE user_id IN (SELECT user_id FROM platform.users WHERE email LIKE @p)",
            ("p", InvitePrefix + "%"));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id IN (SELECT user_id FROM platform.users WHERE email LIKE @p)",
            ("p", InvitePrefix + "%"));
        await Exec(conn,
            "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = 'deleted+' || user_id || '@inv.test' WHERE email LIKE @p",
            ("p", InvitePrefix + "%"));

        var users = new[] { AdminUserId, InviterUserId };
        var emails = new[] { AdminEmail, InviterEmail };
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.role_permissions WHERE role_id = @rid", ("rid", InviterRoleId));
        await Exec(conn, "DELETE FROM platform.roles WHERE role_id = @rid", ("rid", InviterRoleId));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = ANY(@e)", ("e", emails));

        foreach (var uid in users)
            await Exec(conn,
                "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                ("anon", $"deleted+{uid}@inv.test"), ("u", uid));

        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = ANY(@t)",
            ("t", new[] { TenantId, OtherTenantId }));
        await base.DisposeAsync();
    }

    /// <summary>Reads one column of an invitation via the RLS-exempt owner connection (assertion only).</summary>
    public static async Task<object?> InvitationScalarAsync(Guid invitationId, string column)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {column} FROM platform.invitations WHERE invitation_id = @i", conn);
        cmd.Parameters.AddWithValue("i", invitationId);
        return await cmd.ExecuteScalarAsync();
    }

    /// <summary>True if a user with this email exists and is not soft-deleted (proves provisioning / rollback).</summary>
    public static async Task<bool> LiveUserExistsAsync(string email)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM platform.users WHERE email = @e AND deleted_at IS NULL)", conn);
        cmd.Parameters.AddWithValue("e", email);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Number of ACTIVE role assignments a user holds in a tenant (proves accept assigned the role).</summary>
    public static async Task<int> ActiveRoleCountAsync(string email, Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT COUNT(*) FROM platform.user_tenant_roles utr
            JOIN platform.users u ON u.user_id = utr.user_id
            WHERE u.email = @e AND utr.tenant_id = @t AND utr.revoked_at IS NULL
            """, conn);
        cmd.Parameters.AddWithValue("e", email);
        cmd.Parameters.AddWithValue("t", tenantId);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
