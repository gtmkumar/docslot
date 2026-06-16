using System.Threading;
using mediq.Application.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Boots the real <c>mediq.Api</c> pipeline against the live, canonical <c>docslot_platform</c> database
/// (ADR-007: SQL is truth; the DB already has all 113 tables). Seeds a known super_admin user + platform
/// role assignment in <c>InitializeAsync</c> and removes only those seeded rows in <c>DisposeAsync</c> —
/// the schema itself is NEVER mutated.
/// <para>
/// It also swaps <see cref="IRbacQueryService"/> for a counting decorator so a test can prove the
/// effective permission set is resolved EXACTLY ONCE per request (NFR-PERF-01 / resolve-once).
/// </para>
/// </summary>
public sealed class PlatformWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";

    public const string SuperAdminPassword = "Sup3rSecret!";

    public Guid SuperAdminUserId { get; } = Guid.NewGuid();
    public Guid TenantId { get; } = Guid.NewGuid();

    /// <summary>A real, active tenant the seeded user is NOT a member of — target for the tenant-spoof test.</summary>
    public Guid OtherTenantId { get; } = Guid.NewGuid();

    /// <summary>Unique per fixture instance so reruns never collide on the email UNIQUE index.</summary>
    public string SuperAdminEmail { get; } = $"slice01.superadmin+{Guid.NewGuid():N}@docslot.test";

    /// <summary>
    /// Global resolver call counter (server-side). The test resets it, issues one request, and asserts it
    /// incremented exactly once — proving the effective set is resolved once per request, not per check.
    /// </summary>
    public static int ResolveCallCount;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            // Replace the real RBAC service registration with a counting decorator (manual, no Scrutor).
            var original = services.Single(d => d.ServiceType == typeof(IRbacQueryService));
            services.Remove(original);
            services.AddScoped<IRbacQueryService>(sp =>
            {
                var inner = (IRbacQueryService)ActivatorUtilities.CreateInstance(
                    sp, typeof(mediq.Infrastructure.Rbac.RbacQueryService));
                return new CountingRbacQueryService(inner);
            });
        });
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        // 1) A real tenant (hospital) so menus + tenant-scoped resolution have a target.
        await Exec(conn,
            """
            INSERT INTO platform.tenants
                (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (@id, @code, 'Slice01 Test Hospital', 'Slice01 Hospital', 'hospital',
                    'tenant.slice01@docslot.test', '+919999999999', 'active')
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            ("id", TenantId), ("code", $"slice01-{TenantId.ToString()[..8]}"));

        // 1b) A SECOND active tenant the seeded user is deliberately NOT a member of (tenant-spoof target).
        await Exec(conn,
            """
            INSERT INTO platform.tenants
                (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (@id, @code, 'Slice01 Other Hospital', 'Slice01 Other', 'hospital',
                    'other.slice01@docslot.test', '+919888888888', 'active')
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            ("id", OtherTenantId), ("code", $"slice01o-{OtherTenantId.ToString()[..8]}"));

        // 2) A super_admin user with a pgcrypto bcrypt hash (proves bcrypt verify compatibility).
        await Exec(conn,
            """
            INSERT INTO platform.users
                (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, created_at, updated_at)
            VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Slice01 Super Admin', true, true, true, NOW(), NOW())
            ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
            """,
            ("id", SuperAdminUserId), ("email", SuperAdminEmail), ("pwd", SuperAdminPassword));

        // 3) Platform-level super_admin role assignment (tenant_id NULL = platform scope).
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, NULL, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = 'super_admin' AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", SuperAdminUserId));

        // 4) Also give the user a tenant_owner assignment IN the test tenant so /me/menus has a tenant
        //    context whose tenant_type ('hospital') exercises the tenant-type menu filter.
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, @tid, r.role_id, false, NOW()
            FROM platform.roles r WHERE r.role_key = 'tenant_owner' AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", SuperAdminUserId), ("tid", TenantId));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        // Remove the transient seed rows. The user CANNOT be hard-deleted because audit_log references it
        // by FK and audit rows are immutable (never DELETE — DocSlot invariant), so we soft-delete + anonymize
        // the seed user instead. We also null the impersonator/user FKs are not touched; audit history stays intact.
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = @uid", ("uid", SuperAdminUserId));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = @uid", ("uid", SuperAdminUserId));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = @e", ("e", SuperAdminEmail));
        await Exec(conn,
            "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @uid",
            ("anon", $"deleted+{SuperAdminUserId}@slice01.test"), ("uid", SuperAdminUserId));
        // The tenant is referenced by immutable audit_log rows (login wrote tenant_id), so soft-delete it
        // too rather than hard-delete — consistent with the DocSlot "never break the audit trail" invariant.
        await Exec(conn,
            "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = @tid",
            ("tid", TenantId));
        // The "other" tenant may be referenced by an immutable audit_log row (the switch_tenant_denied
        // entry records the attempted tenant), so soft-delete it too rather than hard-delete.
        await Exec(conn,
            "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = @tid AND deleted_at IS NULL",
            ("tid", OtherTenantId));
        await Exec(conn,
            "DELETE FROM platform.tenants WHERE tenant_id = @tid AND NOT EXISTS (SELECT 1 FROM platform.audit_log WHERE tenant_id = @tid)",
            ("tid", OtherTenantId));
        await base.DisposeAsync();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>Counting decorator over the real RBAC service — increments the global counter on resolve.</summary>
public sealed class CountingRbacQueryService(IRbacQueryService inner) : IRbacQueryService
{
    public Task<IReadOnlySet<string>> ResolvePermissionsAsync(Guid userId, Guid? tenantId, CancellationToken ct)
    {
        Interlocked.Increment(ref PlatformWebAppFactory.ResolveCallCount);
        return inner.ResolvePermissionsAsync(userId, tenantId, ct);
    }

    public Task<IReadOnlyList<mediq.SharedDataModel.Docslot.Navigation.MenuNodeDto>> GetMenusAsync(
        Guid userId, Guid tenantId, string? tenantType, string productKey, CancellationToken ct)
        => inner.GetMenusAsync(userId, tenantId, tenantType, productKey, ct);

    public Task<bool> HasPermissionAsync(Guid userId, string permissionKey, Guid? tenantId, CancellationToken ct)
        => inner.HasPermissionAsync(userId, permissionKey, tenantId, ct);
}
