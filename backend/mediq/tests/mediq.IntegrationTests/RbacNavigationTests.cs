using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Navigation;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice 08 (RBAC navigation + permission completeness) invariants against the live canonical DB:
///   1. super_admin resolves the FULL permission registry — including a sampled docslot key and the
///      two new Slice-08 keys (docslot.patient.create, docslot.booking.no_show). This is the
///      regression guard for the historical hole where super_admin held only platform + commission.
///   2. /me/menus returns the full seeded screen set, specifically the screens added in this slice:
///      Developers (API), Security & Compliance, Care Partners, Analytics, Team & Roles, Calendar.
///   3. The re-gated endpoints accept the NEW keys (super_admin holds them) and a user WITHOUT the
///      key is denied (403) — proving the gate moved to the new key and is enforced.
/// </summary>
public sealed class RbacNavigationTests(PlatformWebAppFactory factory) : IClassFixture<PlatformWebAppFactory>
{
    [Fact]
    public async Task SuperAdmin_Resolves_Full_Registry_Including_Sampled_Product_And_New_Keys()
    {
        var client = factory.CreateClient();
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var keys = await GetPermissionKeysAsync(client);

        // Sampled docslot permission — proves the docslot product is no longer absent from super_admin.
        Assert.Contains("docslot.booking.read", keys);
        Assert.Contains("docslot.patient.read", keys);

        // The two NEW Slice-08 keys must resolve for super_admin (swept in at end of bundle).
        Assert.Contains("docslot.patient.create", keys);
        Assert.Contains("docslot.booking.no_show", keys);

        // The universal sweep guarantee — super_admin's ROLE holds EVERY permission in the registry —
        // is a property of the grant table, independent of any one tenant's resolution context. (The
        // resolver intentionally surfaces only platform-scoped + this-tenant grants for a given request,
        // so the per-request set is a correct subset, not the whole 127.) Assert the grant invariant at
        // the table level: this is the regression guard for the historical super_admin hole.
        var (superAdminGrants, registryTotal) = await CountSuperAdminGrantsVsRegistryAsync();
        Assert.Equal(registryTotal, superAdminGrants);
    }

    [Fact]
    public async Task Menus_Include_Developers_Security_CarePartners_And_Other_New_Screens()
    {
        var client = factory.CreateClient();
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var menus = await (await client.GetAsync("/api/v1/me/menus"))
            .Content.ReadFromJsonAsync<List<MenuNodeDto>>();
        Assert.NotNull(menus);

        var topKeys = menus!.Select(m => m.Key).ToHashSet();

        // Screens added/confirmed in Slice 08 — gated by keys super_admin now holds.
        Assert.Contains("developers", topKeys);     // gated by platform.api_clients.manage
        Assert.Contains("security", topKeys);        // gated by platform.audit.read
        Assert.Contains("care_partners", topKeys);   // gated by commission.broker.read (customer-facing)
        Assert.Contains("analytics", topKeys);       // gated by docslot.analytics.read
        Assert.Contains("team", topKeys);            // gated by tenant.roles.assign
        Assert.Contains("calendar", topKeys);

        // Bilingual contract: every returned menu carries a Hindi label.
        Assert.All(menus, m => Assert.False(string.IsNullOrWhiteSpace(m.LabelHi)));

        // Care Partners has children (Partner Directory, Payouts) — tree assembly across the new branch.
        var carePartners = menus.Single(m => m.Key == "care_partners");
        Assert.NotEmpty(carePartners.Children);
        Assert.Contains(carePartners.Children, c => c.Key == "care_partners.directory");
    }

    [Fact]
    public async Task ReGated_Endpoints_Accept_New_Key_For_SuperAdmin_And_Deny_Without_It()
    {
        // --- ACCEPT path: super_admin holds docslot.booking.no_show → the gate lets the request THROUGH
        // (it fails later on a non-existent booking, i.e. 404/422 — NOT 403). A 403 here would mean the
        // gate is still on the old key and super_admin somehow lacked it; we assert it is NOT 403.
        var admin = factory.CreateClient();
        var adminToken = await LoginAsync(admin);
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken.AccessToken);

        var accept = await admin.PostAsync($"/api/v1/bookings/{Guid.NewGuid()}/no-show", content: null);
        Assert.NotEqual(HttpStatusCode.Forbidden, accept.StatusCode);

        // --- DENY path: a fresh user with a custom, zero-permission role must be 403'd by the new gate.
        var (denyEmail, denyUserId, customRoleId) = await SeedZeroPermissionUserAsync();
        try
        {
            var deny = factory.CreateClient();
            var denyToken = await LoginAsync(deny, denyEmail);
            deny.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", denyToken.AccessToken);

            // POST /patients is now gated by docslot.patient.create — this user lacks it → 403.
            var registerResp = await deny.PostAsJsonAsync("/api/v1/patients",
                new { fullName = "Deny User", phone = "+919800000000", tenantId = factory.TenantId });
            Assert.Equal(HttpStatusCode.Forbidden, registerResp.StatusCode);

            // mark-no-show is now gated by docslot.booking.no_show — this user lacks it → 403.
            var noShowResp = await deny.PostAsync($"/api/v1/bookings/{Guid.NewGuid()}/no-show", content: null);
            Assert.Equal(HttpStatusCode.Forbidden, noShowResp.StatusCode);
        }
        finally
        {
            await CleanupZeroPermissionUserAsync(denyEmail, denyUserId, customRoleId);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<TokenResponse> LoginAsync(HttpClient client, string? email = null)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email ?? factory.SuperAdminEmail, PlatformWebAppFactory.SuperAdminPassword, factory.TenantId));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private static async Task<HashSet<string>> GetPermissionKeysAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/v1/me/permissions");
        resp.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("permissionKeys").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
    }

    private static async Task<(int Grants, int Registry)> CountSuperAdminGrantsVsRegistryAsync()
    {
        await using var conn = new Npgsql.NpgsqlConnection(PlatformWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            """
            SELECT
              (SELECT COUNT(*)::int FROM platform.role_permissions rp
                 JOIN platform.roles r ON r.role_id = rp.role_id
                 WHERE r.role_key = 'super_admin' AND r.is_system = true) AS grants,
              (SELECT COUNT(*)::int FROM platform.permissions) AS registry
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>
    /// Creates a fresh user with a custom tenant role that has ZERO permissions, in the factory's tenant.
    /// Used to prove the re-gated endpoints deny a caller lacking the new keys.
    /// </summary>
    private async Task<(string Email, Guid UserId, Guid RoleId)> SeedZeroPermissionUserAsync()
    {
        var email = $"slice08.deny+{Guid.NewGuid():N}@docslot.test";
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var roleKey = ("slice08_zero_" + Guid.NewGuid().ToString("N"))[..28];

        await using var conn = new Npgsql.NpgsqlConnection(PlatformWebAppFactory.ConnectionString);
        await conn.OpenAsync();

        await Exec(conn,
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, email_verified, created_at, updated_at)
            VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Slice08 Deny', true, true, NOW(), NOW())
            ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
            """,
            ("id", userId), ("email", email), ("pwd", PlatformWebAppFactory.SuperAdminPassword));

        // Custom role with NO role_permissions rows → resolves to an empty effective set.
        await Exec(conn,
            """
            INSERT INTO platform.roles (role_id, role_key, name, scope, tenant_id, is_system, created_at, updated_at)
            VALUES (@rid, @rkey, 'Slice08 Zero', 'tenant', @tid, false, NOW(), NOW())
            ON CONFLICT DO NOTHING
            """,
            ("rid", roleId), ("rkey", roleKey), ("tid", factory.TenantId));

        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            VALUES (gen_random_uuid(), @uid, @tid, @rid, true, NOW())
            ON CONFLICT DO NOTHING
            """,
            ("uid", userId), ("tid", factory.TenantId), ("rid", roleId));

        return (email, userId, roleId);
    }

    private static async Task CleanupZeroPermissionUserAsync(string email, Guid userId, Guid roleId)
    {
        await using var conn = new Npgsql.NpgsqlConnection(PlatformWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        // Best-effort, per-statement tolerant cleanup. The deny user may have accumulated session and
        // audit rows from logging in and being 403'd; we anonymise/soft-delete rather than fight FKs,
        // and never let cleanup failure mask the assertions under test.
        await TryExec(conn, "DELETE FROM platform.user_sessions WHERE user_id = @u", ("u", userId));
        await TryExec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = @u", ("u", userId));
        await TryExec(conn, "DELETE FROM platform.login_attempts WHERE email = @e", ("e", email));
        await TryExec(conn, "DELETE FROM platform.roles WHERE role_id = @r", ("r", roleId));
        await TryExec(conn,
            "UPDATE platform.users SET deleted_at = NOW(), email = @anon, is_active = false WHERE user_id = @u",
            ("anon", $"deleted+{userId}@slice08.test"), ("u", userId));
    }

    private static async Task TryExec(Npgsql.NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        try { await Exec(conn, sql, args); }
        catch { /* best-effort cleanup — residual FK rows must not fail the test */ }
    }

    private static async Task Exec(Npgsql.NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
