using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Fixture for the tenant SECURITY-POLICY subsystem (issue #91). Seeds (as the DB OWNER — RLS-exempt, setup
/// only) TWO tenants and a cast whose permission tiers exercise every enforcement path. The app under test runs
/// as <c>docslot_app</c> (RLS-enforced) — the production path.
/// <list type="bullet">
/// <item><b>Owner</b> (tenant_owner in Tenant A) — holds tenant.settings.read/.update; plus a grant-override for
/// <c>platform.ip_allowlist.manage</c> (tenant_owner does NOT get it by default: it was seeded after the owner
/// grant). Covered by the owners_admins MFA tier.</item>
/// <item><b>Doctor</b> (doctor role in Tenant A) — holds docslot.doctor.read_self (login-hours exempt) and
/// docslot.medical_history.read (masking exempt). NOT in the owners_admins tier.</item>
/// <item><b>Receptionist</b> (tenant_staff in Tenant A) — holds docslot.patient.read but NOT medical_history.read,
/// NOT doctor.read_self, NOT tenant.users.update. The front-desk masking / hours / MFA-all target.</item>
/// <item><b>MfaUser</b> (tenant_staff in Tenant A, <c>mfa_enabled=true</c>) — the MFA-tier positive case.</item>
/// <item><b>PwdUser</b> (tenant_staff in Tenant A) — isolated so the change-password test can mutate its credential.</item>
/// <item><b>OwnerB</b> (tenant_owner in Tenant B) — proves policy tenant-isolation.</item>
/// </list>
/// A consented patient is linked to Tenant A for the receptionist-masking test. A tiny test-only middleware sets
/// the connection's source IP from an <c>X-Test-Ip</c> header so the IP allow-list block/allow paths are
/// deterministic under the in-memory TestServer.
/// </summary>
public sealed class SecurityPolicyWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string OwnerConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string Password = "Sup3rSecret!";

    public Guid TenantA { get; } = Guid.NewGuid();
    public Guid TenantB { get; } = Guid.NewGuid();

    public Guid OwnerUserId { get; } = Guid.NewGuid();
    public Guid DoctorUserId { get; } = Guid.NewGuid();
    public Guid ReceptionistUserId { get; } = Guid.NewGuid();
    public Guid MfaUserId { get; } = Guid.NewGuid();
    public Guid PwdUserId { get; } = Guid.NewGuid();
    public Guid OwnerBUserId { get; } = Guid.NewGuid();

    public string OwnerEmail { get; } = $"pol.owner+{Guid.NewGuid():N}@docslot.test";
    public string DoctorEmail { get; } = $"pol.doctor+{Guid.NewGuid():N}@docslot.test";
    public string ReceptionistEmail { get; } = $"pol.recept+{Guid.NewGuid():N}@docslot.test";
    public string MfaEmail { get; } = $"pol.mfa+{Guid.NewGuid():N}@docslot.test";
    public string PwdEmail { get; } = $"pol.pwd+{Guid.NewGuid():N}@docslot.test";
    public string OwnerBEmail { get; } = $"pol.ownerb+{Guid.NewGuid():N}@docslot.test";

    public Guid PatientId { get; } = Guid.NewGuid();
    public string PatientPhone { get; } = $"+9197{Random.Shared.Next(10000000, 99999999)}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Test-only shim: set the connection's source IP from X-Test-Ip so the IP allow-list gate is testable.
        builder.ConfigureServices(services =>
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new TestIpStartupFilter()));
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var (tid, name) in new[] { (TenantA, "Policy A"), (TenantB, "Policy B") })
            await Exec(conn,
                """
                INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
                VALUES (@id, @code, @name, @name, 'hospital', @email, '+919744444444', 'active')
                ON CONFLICT (tenant_id) DO NOTHING
                """,
                ("id", tid), ("code", $"pol-{tid.ToString()[..8]}"), ("name", name), ("email", $"{tid.ToString()[..8]}@pol.test"));

        foreach (var (uid, email, mfa) in new[]
        {
            (OwnerUserId, OwnerEmail, false), (DoctorUserId, DoctorEmail, false),
            (ReceptionistUserId, ReceptionistEmail, false), (MfaUserId, MfaEmail, true),
            (PwdUserId, PwdEmail, false), (OwnerBUserId, OwnerBEmail, false),
        })
            await Exec(conn,
                """
                INSERT INTO platform.users (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, mfa_enabled, created_at, updated_at)
                VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Policy Test User', true, true, false, @mfa, NOW(), NOW())
                ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL, mfa_enabled = EXCLUDED.mfa_enabled
                """,
                ("id", uid), ("email", email), ("pwd", Password), ("mfa", mfa));

        await AddSystemRole(conn, OwnerUserId, TenantA, "tenant_owner");
        await AddSystemRole(conn, DoctorUserId, TenantA, "doctor");
        await AddSystemRole(conn, ReceptionistUserId, TenantA, "tenant_staff");
        await AddSystemRole(conn, MfaUserId, TenantA, "tenant_staff");
        await AddSystemRole(conn, PwdUserId, TenantA, "tenant_staff");
        await AddSystemRole(conn, OwnerBUserId, TenantB, "tenant_owner");

        // tenant_owner does NOT hold platform.ip_allowlist.manage by default → grant it to the owner via a
        // tenant-scoped grant-override (is_allowed=true) so the IP-allowlist management API is reachable.
        await Exec(conn,
            """
            INSERT INTO platform.user_permission_overrides (user_id, permission_id, tenant_id, is_allowed, reason, granted_by_user_id)
            SELECT @u, p.permission_id, @t, true, 'test: ip-allowlist manage', @u
            FROM platform.permissions p WHERE p.permission_key = 'platform.ip_allowlist.manage'
            ON CONFLICT (user_id, permission_id, tenant_id) DO UPDATE SET is_allowed = true, is_active = true, revoked_at = NULL
            """,
            ("u", OwnerUserId), ("t", TenantA));

        // A consented patient linked to Tenant A (receptionist-masking test).
        await Exec(conn,
            """
            INSERT INTO docslot.patients (patient_id, phone_number, full_name, age, gender, preferred_language, consent_given_at, consent_version, is_active, created_at, updated_at)
            VALUES (@id, @phone, 'Masking Test Patient', 42, 'female', 'en', NOW(), 'v1', true, NOW(), NOW())
            ON CONFLICT (phone_number) DO NOTHING
            """,
            ("id", PatientId), ("phone", PatientPhone));
        await Exec(conn,
            """
            INSERT INTO docslot.patient_tenant_links (link_id, patient_id, tenant_id, first_visit_at, last_visit_at, total_visits)
            VALUES (gen_random_uuid(), @pid, @tid, NOW(), NOW(), 0)
            ON CONFLICT (patient_id, tenant_id) DO NOTHING
            """,
            ("pid", PatientId), ("tid", TenantA));
    }

    /// <summary>
    /// Writes <c>tenants.settings-&gt;'security'</c> DIRECTLY (owner connection, bypassing the app) so a test can
    /// establish the exact policy it needs regardless of run order — and, crucially, without needing to first log
    /// in (a blocking policy would otherwise lock the actor out of the very API that sets it).
    /// </summary>
    public async Task WritePolicyAsync(Guid tenantId, object policy)
    {
        var json = JsonSerializer.Serialize(policy);
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await Exec(conn,
            "UPDATE platform.tenants SET settings = jsonb_set(COALESCE(settings,'{}'::jsonb), '{security}', @j::jsonb, true) WHERE tenant_id = @t",
            ("t", tenantId), ("j", json));
    }

    /// <summary>Clears the security policy (back to code defaults) — used by the API GET/PUT tests.</summary>
    public async Task ClearPolicyAsync(Guid tenantId)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await Exec(conn, "UPDATE platform.tenants SET settings = settings - 'security' WHERE tenant_id = @t", ("t", tenantId));
    }

    public async Task SeedAllowlistAsync(Guid tenantId, string cidr)
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await Exec(conn,
            "INSERT INTO platform.ip_allowlist (tenant_id, user_id, cidr_range, label, is_active) VALUES (@t, NULL, CAST(@c AS cidr), 'test', true)",
            ("t", tenantId), ("c", cidr));
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

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        var users = new[] { OwnerUserId, DoctorUserId, ReceptionistUserId, MfaUserId, PwdUserId, OwnerBUserId };
        var emails = new[] { OwnerEmail, DoctorEmail, ReceptionistEmail, MfaEmail, PwdEmail, OwnerBEmail };
        var tenants = new[] { TenantA, TenantB };

        await Exec(conn, "DELETE FROM platform.user_permission_overrides WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.ip_allowlist WHERE tenant_id = ANY(@t)", ("t", tenants));
        await Exec(conn, "DELETE FROM platform.purpose_of_use_log WHERE tenant_id = ANY(@t)", ("t", tenants));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = ANY(@e)", ("e", emails));
        await Exec(conn, "DELETE FROM docslot.patient_tenant_links WHERE patient_id = @p", ("p", PatientId));
        await Exec(conn, "DELETE FROM docslot.patients WHERE patient_id = @p", ("p", PatientId));
        foreach (var uid in users)
            await Exec(conn, "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                ("anon", $"deleted+{uid}@pol.test"), ("u", uid));
        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = ANY(@t)", ("t", tenants));

        await base.DisposeAsync();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Inserts a front-of-pipeline middleware that overrides the source IP from the X-Test-Ip header.</summary>
    private sealed class TestIpStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nextMw) =>
            {
                var hdr = ctx.Request.Headers["X-Test-Ip"].ToString();
                if (!string.IsNullOrWhiteSpace(hdr) && IPAddress.TryParse(hdr, out var ip))
                    ctx.Connection.RemoteIpAddress = ip;
                await nextMw();
            });
            next(app);
        };
    }
}
