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
