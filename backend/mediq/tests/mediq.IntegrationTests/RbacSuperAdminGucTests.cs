using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.SharedDataModel.Docslot.Auth;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// End-to-end proof of the <c>app.is_super_admin</c> GUC wiring (UnitOfWork → set_config per transaction).
/// Both personas hit <c>GET /api/v1/roles?tenantId={B}</c>: the SQL filter admits tenant B's roles, then RLS
/// (<c>roles_read</c> → <c>rls_can_see_tenant</c>) decides. A platform super_admin's request carries
/// <c>app.is_super_admin=true</c> and sees B's custom role across the tenant boundary; a tenant_owner's request
/// carries false and is confined to its own tenant + global rows — so it must NOT see B's role even though it
/// asked for it (cross-tenant enumeration is blocked by RLS, not by the endpoint).
/// </summary>
public sealed class RbacSuperAdminGucTests(RbacSuperAdminGucWebAppFactory factory)
    : IClassFixture<RbacSuperAdminGucWebAppFactory>
{
    [Fact]
    public async Task Super_Admin_Sees_Foreign_Tenant_Role_Cross_Tenant()
    {
        var client = await AuthedClientAsync(factory.SuperAdminEmail);

        var roles = await GetRolesForTenantAsync(client, factory.TenantBId);

        Assert.Contains(roles, r => r.RoleId == factory.TenantBRoleId);
    }

    [Fact]
    public async Task Tenant_Owner_Cannot_See_Foreign_Tenant_Role()
    {
        var client = await AuthedClientAsync(factory.ControlEmail);

        var roles = await GetRolesForTenantAsync(client, factory.TenantBId);

        // RLS hides tenant B's custom role from a tenant-A admin...
        Assert.DoesNotContain(roles, r => r.RoleId == factory.TenantBRoleId);
        // ...but the call itself succeeded and still returns the visible (system/global) roles.
        Assert.Contains(roles, r => r.IsSystem);
    }

    /// <summary>
    /// A PLATFORM-ONLY super_admin (tenant NULL role, ZERO tenant memberships — the prod bootstrap shape)
    /// logs in with no tenant and still gets a backend-driven sidebar: GET /me/menus returns the PLATFORM
    /// scope (global menus filtered by the platform permission set) instead of the historical 403 that
    /// blanked the nav.
    /// </summary>
    [Fact]
    public async Task Platform_Only_SuperAdmin_Without_Tenant_Gets_Platform_Menus()
    {
        var userId = Guid.NewGuid();
        var email = $"guc.platformonly+{Guid.NewGuid():N}@docslot.test";
        await ExecAsync(
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, is_platform_user, created_at, updated_at)
            VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Platform Only SA', true, true, NOW(), NOW())
            """,
            ("id", userId), ("email", email), ("pwd", RbacSuperAdminGucWebAppFactory.Password));
        await ExecAsync(
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, NULL, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = 'super_admin' AND r.is_system = true
            """,
            ("uid", userId));

        try
        {
            var client = factory.CreateClient();
            var login = await client.PostAsJsonAsync("/api/v1/auth/login",
                new LoginRequest(email, RbacSuperAdminGucWebAppFactory.Password, null));
            login.EnsureSuccessStatusCode();
            var token = await login.Content.ReadFromJsonAsync<TokenResponse>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);

            // No active tenant on the session (nothing to auto-pick) …
            var me = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/me");
            Assert.Equal(System.Text.Json.JsonValueKind.Null, me.GetProperty("activeTenantId").ValueKind);

            // … yet menus resolve to the platform scope, not 403.
            var resp = await client.GetAsync("/api/v1/me/menus");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var menus = await resp.Content.ReadFromJsonAsync<List<System.Text.Json.JsonElement>>();
            Assert.NotNull(menus);
            Assert.NotEmpty(menus!);
        }
        finally
        {
            await ExecAsync("DELETE FROM platform.user_tenant_roles WHERE user_id = @u", ("u", userId));
            await ExecAsync("DELETE FROM platform.user_sessions WHERE user_id = @u", ("u", userId));
            await ExecAsync("DELETE FROM platform.login_attempts WHERE email = @e", ("e", email));
            await ExecAsync(
                "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
                ("anon", $"deleted+{userId}@guc.test"), ("u", userId));
        }
    }

    private static async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new Npgsql.NpgsqlConnection(RbacSuperAdminGucWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<IReadOnlyList<RoleDto>> GetRolesForTenantAsync(HttpClient client, Guid tenantId)
    {
        var resp = await client.GetAsync($"/api/v1/roles?tenantId={tenantId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<List<RoleDto>>())!;
    }

    private async Task<HttpClient> AuthedClientAsync(string email)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, RbacSuperAdminGucWebAppFactory.Password, factory.TenantAId));
        login.EnsureSuccessStatusCode();
        var token = await login.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }
}
