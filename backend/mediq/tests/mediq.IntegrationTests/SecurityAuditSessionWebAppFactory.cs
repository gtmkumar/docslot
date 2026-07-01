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
/// Fixture for the Audit-tab read (#86) + active-session oversight (#87) endpoint tests. Seeds, as the DB
/// OWNER (RLS-exempt — setup/teardown only), TWO tenants and a cast of users:
///   • <b>Owner</b> (system <c>tenant_owner</c> in Tenant A) — holds tenant.audit.read AND tenant.users.update.
///   • <b>Viewer</b> (system <c>tenant_viewer</c> in Tenant A) — read-only: has tenant.audit.read, LACKS tenant.users.update.
///   • <b>Zero</b> (empty custom role in Tenant A) — no permissions at all (drives the #86 403).
///   • <b>Member</b> (tenant_viewer in Tenant A) — a member whose sessions are the revoke targets.
///   • <b>Foreign</b> (tenant_owner in Tenant B) — NOT a member of Tenant A; owns a session + a Tenant-B audit row
///     that the Tenant-A reads must never surface.
/// Plus a handful of Tenant-A audit rows (unique <see cref="AuditMarker"/>) spanning categories/severities, and
/// active sessions with known ids. The app under test runs as <c>docslot_app</c> (RLS-enforced), the exact
/// production path. NOTE: <c>platform.audit_log</c> is append-only (a BEFORE UPDATE/DELETE trigger blocks even
/// the owner), so seeded audit rows are NOT torn down — the unique marker keeps assertions exact.
/// </summary>
public sealed class SecurityAuditSessionWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string OwnerConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string Password = "Sup3rSecret!";

    public Guid TenantA { get; } = Guid.NewGuid();
    public Guid TenantB { get; } = Guid.NewGuid();

    public Guid OwnerUserId { get; } = Guid.NewGuid();
    public Guid ViewerUserId { get; } = Guid.NewGuid();
    public Guid ZeroUserId { get; } = Guid.NewGuid();
    public Guid MemberUserId { get; } = Guid.NewGuid();
    public Guid ForeignUserId { get; } = Guid.NewGuid();

    public string OwnerEmail { get; } = $"sec.owner+{Guid.NewGuid():N}@docslot.test";
    public string ViewerEmail { get; } = $"sec.viewer+{Guid.NewGuid():N}@docslot.test";
    public string ZeroEmail { get; } = $"sec.zero+{Guid.NewGuid():N}@docslot.test";
    public string MemberEmail { get; } = $"sec.member+{Guid.NewGuid():N}@docslot.test";
    public string ForeignEmail { get; } = $"sec.foreign+{Guid.NewGuid():N}@docslot.test";

    public Guid ZeroRoleId { get; } = Guid.NewGuid();

    /// <summary>Unique marker stamped into resource_label so the tests find exactly their own audit rows.</summary>
    public string AuditMarker { get; } = $"AUDIT-{Guid.NewGuid():N}";
    /// <summary>Marker on the Tenant-B audit row — must NEVER appear in a Tenant-A read.</summary>
    public string ForeignAuditMarker { get; } = $"FOREIGN-{Guid.NewGuid():N}";

    // Sessions with known ids: two for the member (one asserted present, one revoked), one foreign.
    public Guid MemberSessionListId { get; } = Guid.NewGuid();
    public Guid MemberSessionRevokeId { get; } = Guid.NewGuid();
    public Guid ForeignSessionId { get; } = Guid.NewGuid();
    // A Tenant-A member's session ESTABLISHED under Tenant B — must not list/revoke from Tenant A (#87 hardening).
    public Guid MemberOtherTenantSessionId { get; } = Guid.NewGuid();

    /// <summary>The #94 geo-IP double swapped in for NullGeoIpResolver — defaults to null (offline behaviour);
    /// a test sets <see cref="ConfigurableGeoIpResolver.City"/> to assert a resolved city surfaces.</summary>
    public ConfigurableGeoIpResolver Geo { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGeoIpResolver>();
            services.AddSingleton<IGeoIpResolver>(Geo);
        });
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var (tid, code, name) in new[] { (TenantA, "A", "Sec Audit A"), (TenantB, "B", "Sec Audit B") })
            await Exec(conn,
                """
                INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
                VALUES (@id, @code, @name, @name, 'hospital', @email, '+919755555555', 'active')
                ON CONFLICT (tenant_id) DO NOTHING
                """,
                ("id", tid), ("code", $"sec-{tid.ToString()[..8]}"), ("name", name), ("email", $"{code}@sec.test"));

        foreach (var (uid, email) in new[]
        {
            (OwnerUserId, OwnerEmail), (ViewerUserId, ViewerEmail), (ZeroUserId, ZeroEmail),
            (MemberUserId, MemberEmail), (ForeignUserId, ForeignEmail),
        })
            await Exec(conn,
                """
                INSERT INTO platform.users (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, created_at, updated_at)
                VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Sec Test User', true, true, false, NOW(), NOW())
                ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
                """,
                ("id", uid), ("email", email), ("pwd", Password));

        // Memberships.
        await AddSystemRole(conn, OwnerUserId, TenantA, "tenant_owner");
        await AddSystemRole(conn, ViewerUserId, TenantA, "tenant_viewer");
        await AddSystemRole(conn, MemberUserId, TenantA, "tenant_viewer");
        await AddSystemRole(conn, ForeignUserId, TenantB, "tenant_owner");

        // Zero user: an empty, platform-less custom tenant role in Tenant A → resolves NO permissions.
        await Exec(conn,
            """
            INSERT INTO platform.roles (role_id, role_key, name, tenant_id, scope, is_system, created_at, updated_at)
            VALUES (@id, @key, 'Sec Zero Role', @tid, 'tenant', false, NOW(), NOW())
            ON CONFLICT (role_id) DO NOTHING
            """,
            ("id", ZeroRoleId), ("key", $"sec_zero_{ZeroRoleId.ToString("N")[..8]}"), ("tid", TenantA));
        await Exec(conn,
            "INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at) VALUES (gen_random_uuid(), @u, @tid, @r, true, NOW()) ON CONFLICT DO NOTHING",
            ("u", ZeroUserId), ("tid", TenantA), ("r", ZeroRoleId));

        // Audit rows in Tenant A spanning categories + severities (actor = Owner). success/action drive severity.
        await SeedAudit(conn, TenantA, OwnerUserId, "view", "patient", true, $"Patient view {AuditMarker}");        // Patients / Informational
        await SeedAudit(conn, TenantA, OwnerUserId, "delete", "booking", false, $"Booking delete {AuditMarker}");   // Bookings / Critical
        await SeedAudit(conn, TenantA, OwnerUserId, "update", "tenant_settings", true, $"Settings {AuditMarker}");   // Settings / Informational
        await SeedAudit(conn, TenantA, OwnerUserId, "break_glass", "prescription", true, $"Emergency {AuditMarker}");// Patients / Warning
        // A row in Tenant B — must never leak into a Tenant-A read.
        await SeedAudit(conn, TenantB, ForeignUserId, "view", "patient", true, $"Foreign {ForeignAuditMarker}");

        // Active sessions.
        await SeedSession(conn, MemberSessionListId, MemberUserId, TenantA);
        await SeedSession(conn, MemberSessionRevokeId, MemberUserId, TenantA);
        await SeedSession(conn, ForeignSessionId, ForeignUserId, TenantB);
        await SeedSession(conn, MemberOtherTenantSessionId, MemberUserId, TenantB);
    }

    private static async Task AddSystemRole(NpgsqlConnection conn, Guid userId, Guid tenantId, string roleKey) =>
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = @rk AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", userId), ("tid", tenantId), ("rk", roleKey));

    private static async Task SeedAudit(
        NpgsqlConnection conn, Guid tenantId, Guid actorId, string action, string resourceType, bool success, string label) =>
        await Exec(conn,
            """
            INSERT INTO platform.audit_log
                (audit_id, occurred_at, user_id, tenant_id, action, resource_type, resource_id, resource_label, ip_address, success)
            VALUES (gen_random_uuid(), NOW(), @u, @t, @a, @rt, gen_random_uuid(), @label, CAST('203.0.113.7' AS inet), @ok)
            """,
            ("u", actorId), ("t", tenantId), ("a", action), ("rt", resourceType), ("label", label), ("ok", success));

    private static async Task SeedSession(NpgsqlConnection conn, Guid sessionId, Guid userId, Guid tenantId) =>
        await Exec(conn,
            """
            INSERT INTO platform.user_sessions
                (session_id, user_id, token_hash, active_tenant_id, ip_address, issued_at, expires_at, last_activity_at)
            VALUES (@sid, @uid, encode(gen_random_bytes(32), 'hex'), @tid, CAST('203.0.113.7' AS inet), NOW(), NOW() + INTERVAL '1 day', NOW())
            ON CONFLICT (session_id) DO NOTHING
            """,
            ("sid", sessionId), ("uid", userId), ("tid", tenantId));

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        var users = new[] { OwnerUserId, ViewerUserId, ZeroUserId, MemberUserId, ForeignUserId };
        var emails = new[] { OwnerEmail, ViewerEmail, ZeroEmail, MemberEmail, ForeignEmail };

        // NOTE: platform.audit_log is append-only (trigger blocks DELETE even for the owner) → seeded audit
        // rows are intentionally left behind; the unique marker keeps them from colliding with anything.
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.roles WHERE role_id = @r", ("r", ZeroRoleId));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = ANY(@e)", ("e", emails));
        foreach (var uid in users)
            await Exec(conn, "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                ("anon", $"deleted+{uid}@sec.test"), ("u", uid));
        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = ANY(@t)",
            ("t", new[] { TenantA, TenantB }));

        await base.DisposeAsync();
    }

    /// <summary>True if the session is still live (not revoked, not expired) — read via the owner connection.</summary>
    public static async Task<bool> SessionIsActiveAsync(Guid sessionId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM platform.user_sessions WHERE session_id = @s AND revoked_at IS NULL AND expires_at > NOW())", conn);
        cmd.Parameters.AddWithValue("s", sessionId);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
