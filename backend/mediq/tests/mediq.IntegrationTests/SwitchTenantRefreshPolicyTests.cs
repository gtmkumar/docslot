using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Security;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Issue #91 auditor BLOCKER — the tenant security-policy gate must bind the two token-REISSUE paths, not just
/// login. A session must never mint a fresh tenant-scoped access token while dodging the target tenant's MFA
/// tier / login-hours window / IP allow-list.
///
/// <para><b>switch-tenant</b> enforces the policy of the tenant being switched INTO (the target): a no-MFA user
/// under <c>mfaPolicy=all</c>, an out-of-hours attempt, and a blocked-IP attempt are all 403'd; a compliant user
/// / unhardened target still succeeds.</para>
///
/// <para><b>refresh</b> re-enforces the TIME/CONTEXT-sensitive checks against the session's active tenant so a
/// policy TIGHTENED after login binds the existing session on its next rotation: renewal is blocked after-hours
/// and from a now-blocked IP; a compliant session still renews.</para>
///
/// The app under test runs as <c>docslot_app</c> (RLS-enforced) — the production path. The dedicated cast is
/// multi-tenant (each user is a member of an unhardened HOME tenant and a hardenable TARGET tenant) which the
/// SecurityPolicy fixture's single-tenant cast cannot express. Policies are written directly to
/// <c>tenants.settings-&gt;'security'</c> (owner connection) so a blocking policy never locks the actor out of the
/// login that seeds the session.
/// </summary>
public sealed class SwitchTenantRefreshPolicyTests(SwitchTenantRefreshPolicyWebAppFactory factory)
    : IClassFixture<SwitchTenantRefreshPolicyWebAppFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- switch-tenant: enforce the TARGET tenant's policy ---------------------------------------

    [Fact]
    public async Task SwitchInto_Hardened_Tenant_Blocked_For_NoMfa_User_Under_MfaAll()
    {
        await factory.ClearPolicyAsync(factory.HomeTenant);
        await factory.WritePolicyAsync(factory.TargetTenant, FullPolicy(mfaPolicy: MfaPolicyTiers.All));

        var login = await LoginAsync(factory.NoMfaEmail, factory.HomeTenant);   // HOME is unhardened → login OK
        var switched = await SwitchAsync(login, factory.TargetTenant);

        Assert.Equal(HttpStatusCode.Forbidden, switched.StatusCode);
        Assert.Contains(mediq.Utilities.Exceptions.MfaEnrollmentRequiredException.Code,
            await switched.Content.ReadAsStringAsync());

        await factory.ClearPolicyAsync(factory.TargetTenant);
    }

    [Fact]
    public async Task SwitchInto_Hardened_Tenant_Blocked_Out_Of_Hours()
    {
        await factory.ClearPolicyAsync(factory.HomeTenant);
        // A 1-hour window starting 2h ahead (IST) can never contain "now" → the staff user (no doctor exemption)
        // is out of hours.
        var istNow = DateTime.UtcNow.AddMinutes(330);
        await factory.WritePolicyAsync(factory.TargetTenant, FullPolicy(
            restrictLoginHours: true,
            loginHoursStart: istNow.AddHours(2).ToString("HH:mm"),
            loginHoursEnd: istNow.AddHours(3).ToString("HH:mm")));

        var login = await LoginAsync(factory.NoMfaEmail, factory.HomeTenant);
        var switched = await SwitchAsync(login, factory.TargetTenant);

        Assert.Equal(HttpStatusCode.Forbidden, switched.StatusCode);

        await factory.ClearPolicyAsync(factory.TargetTenant);
    }

    [Fact]
    public async Task SwitchInto_Hardened_Tenant_Blocked_From_Non_Allowlisted_Ip()
    {
        await factory.ClearPolicyAsync(factory.HomeTenant);
        await factory.WritePolicyAsync(factory.TargetTenant, FullPolicy(ipAllowlistEnabled: true));
        await factory.SeedAllowlistAsync(factory.TargetTenant, "203.0.113.0/24");

        var login = await LoginAsync(factory.NoMfaEmail, factory.HomeTenant);   // HOME unhardened → IP not checked
        var switched = await SwitchAsync(login, factory.TargetTenant, sourceIp: "198.51.100.9");

        Assert.Equal(HttpStatusCode.Forbidden, switched.StatusCode);   // switch source IP outside allow-list → blocked

        await factory.ClearPolicyAsync(factory.TargetTenant);
    }

    [Fact]
    public async Task SwitchInto_Unhardened_Tenant_Succeeds()
    {
        await factory.ClearPolicyAsync(factory.HomeTenant);
        await factory.ClearPolicyAsync(factory.TargetTenant);   // compliant / unhardened target

        var login = await LoginAsync(factory.NoMfaEmail, factory.HomeTenant);
        var switched = await SwitchAsync(login, factory.TargetTenant);

        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        var token = await switched.Content.ReadFromJsonAsync<TokenResponse>(Json);
        Assert.Equal(factory.TargetTenant, token!.ActiveTenantId);
    }

    [Fact]
    public async Task SwitchInto_MfaAll_Tenant_Succeeds_For_Mfa_Enrolled_User()
    {
        await factory.ClearPolicyAsync(factory.HomeTenant);
        await factory.WritePolicyAsync(factory.TargetTenant, FullPolicy(mfaPolicy: MfaPolicyTiers.All));

        var login = await LoginAsync(factory.MfaEmail, factory.HomeTenant);   // this user HAS mfa
        var switched = await SwitchAsync(login, factory.TargetTenant);

        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);   // covered by 'all' but enrolled → passes

        await factory.ClearPolicyAsync(factory.TargetTenant);
    }

    // ---- refresh: re-enforce time/context against the session's active tenant --------------------

    [Fact]
    public async Task Refresh_Blocked_Out_Of_Hours_Tightened_After_Login()
    {
        await factory.ClearPolicyAsync(factory.TargetTenant);
        var login = await LoginAsync(factory.NoMfaEmail, factory.TargetTenant);   // session bound to TARGET

        // Policy tightened AFTER the session was minted.
        var istNow = DateTime.UtcNow.AddMinutes(330);
        await factory.WritePolicyAsync(factory.TargetTenant, FullPolicy(
            restrictLoginHours: true,
            loginHoursStart: istNow.AddHours(2).ToString("HH:mm"),
            loginHoursEnd: istNow.AddHours(3).ToString("HH:mm")));

        var refreshed = await RefreshAsync(login.RefreshToken);
        Assert.Equal(HttpStatusCode.Forbidden, refreshed.StatusCode);   // renewal outside now-permitted hours → blocked

        await factory.ClearPolicyAsync(factory.TargetTenant);
    }

    [Fact]
    public async Task Refresh_Blocked_From_Non_Allowlisted_Ip_Tightened_After_Login()
    {
        await factory.ClearPolicyAsync(factory.TargetTenant);
        var login = await LoginAsync(factory.NoMfaEmail, factory.TargetTenant);

        await factory.WritePolicyAsync(factory.TargetTenant, FullPolicy(ipAllowlistEnabled: true));
        await factory.SeedAllowlistAsync(factory.TargetTenant, "203.0.113.0/24");

        var refreshed = await RefreshAsync(login.RefreshToken, sourceIp: "198.51.100.9");
        Assert.Equal(HttpStatusCode.Forbidden, refreshed.StatusCode);   // renewal from now-blocked network → blocked

        await factory.ClearPolicyAsync(factory.TargetTenant);
    }

    [Fact]
    public async Task Refresh_Succeeds_For_Compliant_Session()
    {
        await factory.ClearPolicyAsync(factory.TargetTenant);
        var login = await LoginAsync(factory.NoMfaEmail, factory.TargetTenant);

        var refreshed = await RefreshAsync(login.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var token = await refreshed.Content.ReadFromJsonAsync<TokenResponse>(Json);
        Assert.Equal(factory.TargetTenant, token!.ActiveTenantId);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static object FullPolicy(
        string mfaPolicy = "optional", int minPasswordLength = 8, int idleTimeoutMinutes = 30,
        bool requireNewDeviceVerification = false, bool restrictLoginHours = false,
        string loginHoursStart = "00:00", string loginHoursEnd = "23:59",
        bool doctorsExemptFromHours = true, bool ipAllowlistEnabled = false,
        bool maskSensitiveForReceptionist = true)
        => new
        {
            mfaPolicy, minPasswordLength, idleTimeoutMinutes, requireNewDeviceVerification, restrictLoginHours,
            loginHoursStart, loginHoursEnd, doctorsExemptFromHours, ipAllowlistEnabled, maskSensitiveForReceptionist,
        };

    private async Task<TokenResponse> LoginAsync(string email, Guid tenantId)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, SwitchTenantRefreshPolicyWebAppFactory.Password, tenantId));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
    }

    private async Task<HttpResponseMessage> SwitchAsync(TokenResponse session, Guid tenantId, string? sourceIp = null)
    {
        var client = factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/switch-tenant")
        {
            Content = JsonContent.Create(new SwitchTenantRequest(tenantId, session.RefreshToken)),
        };
        // switch-tenant is [Authorize] — present the HOME access token so the endpoint admits the caller; the
        // membership + policy checks then run off the refresh token + the requested TARGET tenant, exactly as
        // production does (the client can never change its tenant by sending a header).
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
        if (sourceIp is not null) req.Headers.Add("X-Test-Ip", sourceIp);
        return await client.SendAsync(req);
    }

    private async Task<HttpResponseMessage> RefreshAsync(string refreshToken, string? sourceIp = null)
    {
        var client = factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh")
        {
            Content = JsonContent.Create(new RefreshRequest(refreshToken)),
        };
        if (sourceIp is not null) req.Headers.Add("X-Test-Ip", sourceIp);
        return await client.SendAsync(req);
    }
}

/// <summary>
/// Fixture for <see cref="SwitchTenantRefreshPolicyTests"/>. Seeds (as the DB OWNER — RLS-exempt, setup only) TWO
/// tenants and TWO users, each a member of BOTH tenants, so a session opened against an unhardened HOME tenant can
/// be reissued (switch/refresh) against a hardenable TARGET tenant:
/// <list type="bullet">
/// <item><b>NoMfa</b> (tenant_staff in both, <c>mfa_enabled=false</c>) — the MFA-all / hours / IP block target.</item>
/// <item><b>Mfa</b> (tenant_staff in both, <c>mfa_enabled=true</c>) — the MFA-tier positive control.</item>
/// </list>
/// The same <c>X-Test-Ip</c> shim as the SecurityPolicy fixture makes the IP allow-list block/allow paths
/// deterministic under the in-memory TestServer.
/// </summary>
public sealed class SwitchTenantRefreshPolicyWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string OwnerConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string Password = "Sup3rSecret!";

    public Guid HomeTenant { get; } = Guid.NewGuid();
    public Guid TargetTenant { get; } = Guid.NewGuid();

    public Guid NoMfaUserId { get; } = Guid.NewGuid();
    public Guid MfaUserId { get; } = Guid.NewGuid();

    public string NoMfaEmail { get; } = $"sw.nomfa+{Guid.NewGuid():N}@docslot.test";
    public string MfaEmail { get; } = $"sw.mfa+{Guid.NewGuid():N}@docslot.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new TestIpStartupFilter()));
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        foreach (var (tid, name) in new[] { (HomeTenant, "Switch Home"), (TargetTenant, "Switch Target") })
            await Exec(conn,
                """
                INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
                VALUES (@id, @code, @name, @name, 'hospital', @email, '+919744444444', 'active')
                ON CONFLICT (tenant_id) DO NOTHING
                """,
                ("id", tid), ("code", $"sw-{tid.ToString()[..8]}"), ("name", name), ("email", $"{tid.ToString()[..8]}@sw.test"));

        foreach (var (uid, email, mfa) in new[]
        {
            (NoMfaUserId, NoMfaEmail, false), (MfaUserId, MfaEmail, true),
        })
            await Exec(conn,
                """
                INSERT INTO platform.users (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, mfa_enabled, created_at, updated_at)
                VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Switch Test User', true, true, false, @mfa, NOW(), NOW())
                ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL, mfa_enabled = EXCLUDED.mfa_enabled
                """,
                ("id", uid), ("email", email), ("pwd", Password), ("mfa", mfa));

        // Each user is tenant_staff in BOTH tenants; HOME is the primary membership.
        foreach (var uid in new[] { NoMfaUserId, MfaUserId })
        {
            await AddSystemRole(conn, uid, HomeTenant, "tenant_staff", isPrimary: true);
            await AddSystemRole(conn, uid, TargetTenant, "tenant_staff", isPrimary: false);
        }
    }

    public async Task WritePolicyAsync(Guid tenantId, object policy)
    {
        var json = JsonSerializer.Serialize(policy);
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await Exec(conn,
            "UPDATE platform.tenants SET settings = jsonb_set(COALESCE(settings,'{}'::jsonb), '{security}', @j::jsonb, true) WHERE tenant_id = @t",
            ("t", tenantId), ("j", json));
    }

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

    private static async Task AddSystemRole(NpgsqlConnection conn, Guid userId, Guid tenantId, string roleKey, bool isPrimary) =>
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, @tid, r.role_id, @primary, NOW()
            FROM platform.roles r WHERE r.role_key = @rk AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", userId), ("tid", tenantId), ("rk", roleKey), ("primary", isPrimary));

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();

        var users = new[] { NoMfaUserId, MfaUserId };
        var emails = new[] { NoMfaEmail, MfaEmail };
        var tenants = new[] { HomeTenant, TargetTenant };

        await Exec(conn, "DELETE FROM platform.ip_allowlist WHERE tenant_id = ANY(@t)", ("t", tenants));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = ANY(@u)", ("u", users));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = ANY(@e)", ("e", emails));
        foreach (var uid in users)
            await Exec(conn, "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                ("anon", $"deleted+{uid}@sw.test"), ("u", uid));
        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = ANY(@t)", ("t", tenants));

        await base.DisposeAsync();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

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
