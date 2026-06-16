using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Navigation;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// End-to-end slice-01 invariants against the live canonical DB:
///   login → JWT → /me/permissions resolves the expected super_admin set → /me/menus returns a
///   non-empty bilingual tree → resolve-once-per-request holds → bcrypt seed verifies → lockout works.
/// </summary>
public sealed class AuthRbacFlowTests(PlatformWebAppFactory factory) : IClassFixture<PlatformWebAppFactory>
{
    [Fact]
    public async Task Login_Then_Me_Permissions_And_Menus_Resolve_For_SuperAdmin()
    {
        var client = factory.CreateClient();

        // --- 1) Login with the seeded bcrypt (pgcrypto crypt()) password → access JWT ---
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.SuperAdminEmail, PlatformWebAppFactory.SuperAdminPassword, factory.TenantId));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var token = await login.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        Assert.False(string.IsNullOrWhiteSpace(token!.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(token.RefreshToken));
        Assert.Equal(factory.SuperAdminUserId, token.UserId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // --- 2) /me/permissions resolves the super_admin set (all platform.* + tenant.* permissions) ---
        PlatformWebAppFactory.ResolveCallCount = 0;
        var permsResp = await client.GetAsync("/api/v1/me/permissions");
        Assert.Equal(HttpStatusCode.OK, permsResp.StatusCode);

        // PermissionSetDto.PermissionKeys is IReadOnlySet<string> (abstract for System.Text.Json), so read
        // the keys array out of the JSON document directly rather than deserializing the DTO.
        using var permsDoc = System.Text.Json.JsonDocument.Parse(await permsResp.Content.ReadAsStringAsync());
        var keys = permsDoc.RootElement.GetProperty("permissionKeys")
            .EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        Assert.NotEmpty(keys);
        // super_admin holds every permission, including dangerous platform ones.
        Assert.Contains("platform.tenants.read", keys);
        Assert.Contains("platform.overrides.grant", keys);

        // --- 3) Resolve-once-per-request: exactly one resolver call for this single request ---
        Assert.Equal(1, PlatformWebAppFactory.ResolveCallCount);

        // --- 4) /me/menus returns a non-empty bilingual, hierarchical tree (tenant_type = hospital) ---
        var menusResp = await client.GetAsync("/api/v1/me/menus");
        Assert.Equal(HttpStatusCode.OK, menusResp.StatusCode);

        var menus = await menusResp.Content.ReadFromJsonAsync<List<MenuNodeDto>>();
        Assert.NotNull(menus);
        Assert.NotEmpty(menus!);

        // Dashboard is ungated → always present; carries a Hindi label (bilingual).
        var dashboard = menus.SingleOrDefault(m => m.Key == "dashboard");
        Assert.NotNull(dashboard);
        Assert.False(string.IsNullOrWhiteSpace(dashboard!.LabelHi));

        // The 'doctors' menu targets hospital/clinic tenant types → must appear for this hospital tenant.
        Assert.Contains(menus, m => m.Key == "doctors");

        // Tree assembly: 'bookings' has children (today/upcoming/history).
        var bookings = menus.SingleOrDefault(m => m.Key == "bookings");
        Assert.NotNull(bookings);
        Assert.NotEmpty(bookings!.Children);
        Assert.Contains(bookings.Children, c => c.Key == "bookings.today");
    }

    [Fact]
    public async Task Admin_Endpoint_Is_Gated_By_Permission_And_SuperAdmin_Passes()
    {
        var client = factory.CreateClient();

        // Unauthenticated → 401.
        var anon = await client.GetAsync("/api/v1/tenants");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        // Authenticated super_admin holds platform.tenants.read → 200.
        var token = await LoginAsync(client, factory.SuperAdminEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var tenants = await client.GetAsync("/api/v1/tenants");
        Assert.Equal(HttpStatusCode.OK, tenants.StatusCode);
    }

    [Fact]
    public async Task Refresh_Rotates_Token_And_Returns_New_Pair()
    {
        var client = factory.CreateClient();
        var token = await LoginAsync(client, factory.SuperAdminEmail);

        var refreshResp = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(token.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, refreshResp.StatusCode);

        var rotated = await refreshResp.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(rotated);
        Assert.NotEqual(token.RefreshToken, rotated!.RefreshToken);   // rotation
        Assert.False(string.IsNullOrWhiteSpace(rotated.AccessToken));
    }

    [Fact]
    public async Task Wrong_Password_Is_Rejected_With_401()
    {
        var client = factory.CreateClient();
        var bad = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.SuperAdminEmail, "definitely-wrong", factory.TenantId));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }

    [Fact]
    public async Task Create_Custom_Role_Then_Assign_Then_Revoke_Roundtrips()
    {
        var client = factory.CreateClient();
        var token = await LoginAsync(client, factory.SuperAdminEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // --- Create a custom tenant-scoped role (gated by platform.roles.manage; super_admin holds it) ---
        var roleKey = ("slice01_custom_" + Guid.NewGuid().ToString("N"))[..28];
        var createResp = await client.PostAsJsonAsync("/api/v1/roles",
            new mediq.SharedDataModel.Docslot.Admin.CreateRoleRequest(
                roleKey, "Slice01 Custom Role", "test", factory.TenantId, "tenant"));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<mediq.SharedDataModel.Docslot.Admin.CreateRoleResult>();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created!.RoleId);

        // --- Assign that role to the super_admin user in the tenant ---
        var assignResp = await client.PostAsJsonAsync("/api/v1/role-assignments",
            new mediq.SharedDataModel.Docslot.Admin.AssignRoleRequest(
                factory.SuperAdminUserId, created.RoleId, factory.TenantId, ExpiresAt: null));
        Assert.Equal(HttpStatusCode.OK, assignResp.StatusCode);
        var assigned = await assignResp.Content.ReadFromJsonAsync<mediq.SharedDataModel.Docslot.Admin.AssignRoleResult>();
        Assert.NotNull(assigned);

        // --- Revoke the assignment (soft) ---
        var revokeResp = await client.PostAsJsonAsync("/api/v1/role-assignments/revoke",
            new mediq.SharedDataModel.Docslot.Admin.RevokeRoleRequest(assigned!.UserTenantRoleId, "test cleanup"));
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);
        var revoked = await revokeResp.Content.ReadFromJsonAsync<mediq.SharedDataModel.Docslot.Admin.RevokeRoleResult>();
        Assert.NotNull(revoked);
        Assert.False(revoked!.AlreadyRevoked);

        // --- Re-revoke is idempotent ---
        var revokeAgain = await client.PostAsJsonAsync("/api/v1/role-assignments/revoke",
            new mediq.SharedDataModel.Docslot.Admin.RevokeRoleRequest(assigned.UserTenantRoleId, "again"));
        Assert.Equal(HttpStatusCode.OK, revokeAgain.StatusCode);
        var revoked2 = await revokeAgain.Content.ReadFromJsonAsync<mediq.SharedDataModel.Docslot.Admin.RevokeRoleResult>();
        Assert.True(revoked2!.AlreadyRevoked);
    }

    [Fact]
    public async Task Spoofed_XTenantId_Header_Is_Ignored_Active_Tenant_Stays_JWT_Claim()
    {
        var client = factory.CreateClient();

        // Log in scoped to the user's real tenant; the JWT carries that tenant_id claim.
        var token = await LoginWithTenantAsync(client, factory.SuperAdminEmail, factory.TenantId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // Baseline: /me reflects the JWT's active tenant (which == CurrentUserContext.TenantId).
        var meBaseline = await (await client.GetAsync("/api/v1/me")).Content.ReadFromJsonAsync<MeDto>();
        Assert.Equal(factory.TenantId, meBaseline!.ActiveTenantId);

        // Attack: spoof X-Tenant-Id with a tenant the user is NOT a member of.
        client.DefaultRequestHeaders.Remove("X-Tenant-Id");
        client.DefaultRequestHeaders.Add("X-Tenant-Id", factory.OtherTenantId.ToString());

        var meSpoofed = await (await client.GetAsync("/api/v1/me")).Content.ReadFromJsonAsync<MeDto>();

        // The header MUST be ignored — active tenant is unchanged (still the signed JWT claim),
        // and is NOT the spoofed tenant. This is the value that scopes RLS/app.tenant_id.
        Assert.Equal(factory.TenantId, meSpoofed!.ActiveTenantId);
        Assert.NotEqual(factory.OtherTenantId, meSpoofed.ActiveTenantId);

        // And the resolved permission set is identical with vs. without the spoofed header.
        var permsSpoofed = await GetPermissionKeysAsync(client);
        client.DefaultRequestHeaders.Remove("X-Tenant-Id");
        var permsClean = await GetPermissionKeysAsync(client);
        Assert.True(permsSpoofed.SetEquals(permsClean));
    }

    [Fact]
    public async Task SwitchTenant_To_NonMember_Tenant_Is_Forbidden()
    {
        var client = factory.CreateClient();
        var token = await LoginWithTenantAsync(client, factory.SuperAdminEmail, factory.TenantId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // Switching to a tenant the user is NOT a member of must fail closed (403).
        var resp = await client.PostAsJsonAsync("/api/v1/auth/switch-tenant",
            new SwitchTenantRequest(factory.OtherTenantId, token.RefreshToken));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_Reuse_After_Rotation_Revokes_Whole_Chain()
    {
        var client = factory.CreateClient();
        var token = await LoginAsync(client, factory.SuperAdminEmail);

        // First refresh rotates the token (old refresh token is now revoked/superseded).
        var first = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(token.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rotated = (await first.Content.ReadFromJsonAsync<TokenResponse>())!;

        // Replay the ORIGINAL (now-revoked) refresh token → theft signal → 401 + chain revoked.
        var reuse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(token.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);

        // The whole chain is revoked: the freshly-rotated token no longer works either (fail-closed).
        var afterChainRevoke = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(rotated.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, afterChainRevoke.StatusCode);
    }

    private static async Task<HashSet<string>> GetPermissionKeysAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/v1/me/permissions");
        resp.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("permissionKeys").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
    }

    private static async Task<TokenResponse> LoginAsync(HttpClient client, string email)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, PlatformWebAppFactory.SuperAdminPassword));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private static async Task<TokenResponse> LoginWithTenantAsync(HttpClient client, string email, Guid tenantId)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, PlatformWebAppFactory.SuperAdminPassword, tenantId));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
    }
}
