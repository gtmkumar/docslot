using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.SharedDataModel.Docslot.Auth;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// End-to-end tenant onboarding (POST /api/v1/tenants): a PLATFORM-ONLY super_admin (no tenant memberships —
/// the prod bootstrap shape) creates a clinic; the response's one-time owner invitation is redeemed on the
/// public accept endpoint; the new owner then signs in, lands on THEIR tenant with the tenant_owner role, and
/// gets a backend-driven menu. Also proves the permission gate: a mere tenant_owner cannot onboard tenants.
/// </summary>
public sealed class TenantOnboardingTests(RbacSuperAdminGucWebAppFactory factory)
    : IClassFixture<RbacSuperAdminGucWebAppFactory>
{
    [Fact]
    public async Task SuperAdmin_Onboards_Tenant_And_Owner_Accepts_And_Signs_In()
    {
        var saUserId = Guid.NewGuid();
        var saEmail = $"onboard.sa+{Guid.NewGuid():N}@docslot.test";
        var code = $"onboard-{Guid.NewGuid():N}"[..20];
        var ownerEmail = $"onboard.owner+{Guid.NewGuid():N}@docslot.test";
        Guid tenantId = default;
        Guid ownerUserId = default;

        await SeedPlatformOnlySuperAdminAsync(saUserId, saEmail);
        try
        {
            // ---- 1. Platform super_admin (no active tenant) onboards the clinic --------------------
            var sa = await AuthedClientAsync(saEmail, RbacSuperAdminGucWebAppFactory.Password);
            var create = await sa.PostAsJsonAsync("/api/v1/tenants", new CreateTenantRequest(
                code, "Onboard Test Clinic Pvt Ltd", "Onboard Test Clinic", "hospital",
                $"ops+{code}@docslot.test", "+919800000001", "Mumbai", "Maharashtra",
                "400001", 18.938771m, 72.835335m, ownerEmail));
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var created = (await create.Content.ReadFromJsonAsync<CreateTenantResult>())!;
            tenantId = created.TenantId;
            Assert.Equal(code, created.TenantCode);
            Assert.Equal(ownerEmail, created.AdminEmail);
            Assert.False(string.IsNullOrWhiteSpace(created.InviteToken));

            // PIN + geo tag persisted: pin_code column and the settings.geo JSONB pair.
            var geoJson = await ScalarAsync(
                "SELECT pin_code || '|' || COALESCE(settings#>>'{geo,latitude}', '-') || '|' || COALESCE(settings#>>'{geo,longitude}', '-') " +
                "FROM platform.tenants WHERE tenant_id = @t", ("t", tenantId));
            Assert.Equal("400001|18.938771|72.835335", geoJson);

            // A duplicate tenant_code is a 409, not a 500.
            var dup = await sa.PostAsJsonAsync("/api/v1/tenants", new CreateTenantRequest(
                code, "Dup", "Dup", "hospital", $"dup+{code}@docslot.test", "+919800000002", null, null,
                null, null, null, ownerEmail));
            Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

            // ---- 2. The owner redeems the one-time invitation (public endpoint, sets own password) --
            const string ownerPassword = "Own3rPassw0rd!";
            var accept = await factory.CreateClient().PostAsJsonAsync("/api/v1/invitations/accept",
                new AcceptInvitationRequest(created.InviteToken, "Onboard Owner", ownerPassword));
            Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
            var accepted = (await accept.Content.ReadFromJsonAsync<AcceptInvitationResult>())!;
            ownerUserId = accepted.UserId;
            Assert.Equal(tenantId, accepted.TenantId);

            // ---- 3. The owner signs in and lands on THEIR clinic with the tenant_owner role ---------
            var owner = await AuthedClientAsync(ownerEmail, ownerPassword);
            var me = await owner.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/me");
            Assert.Equal(tenantId.ToString(), me.GetProperty("activeTenantId").GetString());
            var roles = me.GetProperty("roles").EnumerateArray()
                .Select(r => r.GetProperty("roleKey").GetString()).ToList();
            Assert.Contains("tenant_owner", roles);

            var menus = await owner.GetAsync("/api/v1/me/menus");
            Assert.Equal(HttpStatusCode.OK, menus.StatusCode);
            Assert.NotEmpty((await menus.Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>())!);

            // ---- 4. Permission gate: the owner (no platform.tenants.create) cannot onboard ----------
            var forbidden = await owner.PostAsJsonAsync("/api/v1/tenants", new CreateTenantRequest(
                $"{code}-x", "Nope", "Nope", "hospital", $"nope+{code}@docslot.test", "+919800000003", null, null,
                null, null, null, $"nope.owner+{code}@docslot.test"));
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        finally
        {
            // Soft-delete the tenant + anonymize the users (house pattern: never hard-delete audited rows).
            await ExecAsync("DELETE FROM platform.invitations WHERE tenant_id = @t", ("t", (object?)tenantId ?? DBNull.Value));
            foreach (var uid in new[] { saUserId, ownerUserId }.Where(u => u != default))
            {
                await ExecAsync("DELETE FROM platform.user_tenant_roles WHERE user_id = @u", ("u", uid));
                await ExecAsync("DELETE FROM platform.user_sessions WHERE user_id = @u", ("u", uid));
                await ExecAsync(
                    "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                    ("anon", $"deleted+{uid}@onboard.test"), ("u", uid));
            }
            await ExecAsync("DELETE FROM platform.login_attempts WHERE email = @e", ("e", saEmail));
            await ExecAsync("DELETE FROM platform.login_attempts WHERE email = @e", ("e", ownerEmail));
            if (tenantId != default)
                await ExecAsync(
                    "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = @t",
                    ("t", tenantId));
        }
    }

    [Fact]
    public async Task CreateTenant_RejectsUnknownTenantType()
    {
        var saUserId = Guid.NewGuid();
        var saEmail = $"onboard.sa2+{Guid.NewGuid():N}@docslot.test";
        await SeedPlatformOnlySuperAdminAsync(saUserId, saEmail);
        try
        {
            var sa = await AuthedClientAsync(saEmail, RbacSuperAdminGucWebAppFactory.Password);
            var resp = await sa.PostAsJsonAsync("/api/v1/tenants", new CreateTenantRequest(
                $"badtype-{Guid.NewGuid():N}"[..16], "Bad Type", "Bad Type", "spaceship",
                "bad@docslot.test", "+919800000004", null, null, null, null, null, "bad.owner@docslot.test"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        }
        finally
        {
            await ExecAsync("DELETE FROM platform.user_tenant_roles WHERE user_id = @u", ("u", saUserId));
            await ExecAsync("DELETE FROM platform.user_sessions WHERE user_id = @u", ("u", saUserId));
            await ExecAsync("DELETE FROM platform.login_attempts WHERE email = @e", ("e", saEmail));
            await ExecAsync(
                "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                ("anon", $"deleted+{saUserId}@onboard.test"), ("u", saUserId));
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------

    private static async Task SeedPlatformOnlySuperAdminAsync(Guid userId, string email)
    {
        await ExecAsync(
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, is_platform_user, created_at, updated_at)
            VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Onboarding SA', true, true, NOW(), NOW())
            """,
            ("id", userId), ("email", email), ("pwd", RbacSuperAdminGucWebAppFactory.Password));
        await ExecAsync(
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, NULL, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = 'super_admin' AND r.is_system = true
            """,
            ("uid", userId));
    }

    private async Task<HttpClient> AuthedClientAsync(string email, string password)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password, null));
        login.EnsureSuccessStatusCode();
        var token = await login.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private static async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new Npgsql.NpgsqlConnection(RbacSuperAdminGucWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new Npgsql.NpgsqlConnection(RbacSuperAdminGucWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : result.ToString();
    }
}
