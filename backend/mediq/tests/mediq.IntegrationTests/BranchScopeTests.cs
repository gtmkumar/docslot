using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.SharedDataModel.Docslot.Auth;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Proves the branch / department membership-SCOPE surface (issue #90, epic #80 Phase C) behaves as designed
/// under RLS as <c>docslot_app</c>: branch lists are tenant-isolated and read-gated; branch creation is gated
/// on tenant.settings.update; set-scope routes through <c>platform.set_membership_scope</c> and mutates ONLY
/// branch_id/department (never role_id); a scoped member surfaces its branch/department in the People list;
/// and — the load-bearing invariant — setting scope NEVER changes a user's effective permissions (scope is a
/// DISPLAY attribute, not an access boundary).
/// </summary>
public sealed class BranchScopeTests(BranchScopeWebAppFactory factory) : IClassFixture<BranchScopeWebAppFactory>
{
    // ---- Branch list: isolation + read gate ------------------------------------------------------

    [Fact]
    public async Task Owner_ListBranches_IsTenantIsolated()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);
        var created = await CreateBranchAsync(client, "Andheri W");

        var resp = await client.GetAsync($"/api/v1/tenants/{factory.TenantId}/branches");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var branches = (await resp.Content.ReadFromJsonAsync<List<BranchDto>>())!;
        Assert.Contains(branches, b => b.BranchId == created.BranchId);
        // The branch seeded under tenant B must never leak into tenant A's list.
        Assert.DoesNotContain(branches, b => b.BranchId == factory.OtherBranchId);
        Assert.DoesNotContain(branches, b => b.Name == factory.OtherBranchName);
    }

    [Fact]
    public async Task NoAccess_ListBranches_Forbidden()
    {
        var client = await AuthedClientAsync(factory.NoAccessEmail);

        var resp = await client.GetAsync($"/api/v1/tenants/{factory.TenantId}/branches");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- Branch create: gate ---------------------------------------------------------------------

    [Fact]
    public async Task Owner_CreateBranch_Persists()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/branches", new CreateBranchRequest("Bandra", "BND"));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = (await resp.Content.ReadFromJsonAsync<CreateBranchResult>())!;
        Assert.NotEqual(Guid.Empty, created.BranchId);

        var list = (await (await client.GetAsync($"/api/v1/tenants/{factory.TenantId}/branches"))
            .Content.ReadFromJsonAsync<List<BranchDto>>())!;
        Assert.Contains(list, b => b.BranchId == created.BranchId && b.Name == "Bandra" && b.Code == "BND");
    }

    [Fact]
    public async Task Viewer_CreateBranch_Forbidden()
    {
        var client = await AuthedClientAsync(factory.ViewerEmail);   // tenant_viewer lacks tenant.settings.update

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/branches", new CreateBranchRequest("Should Fail", null));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- Set scope: writes only branch_id/department, never role_id ------------------------------

    [Fact]
    public async Task Owner_SetScope_UpdatesOnlyBranchAndDepartment_RoleUnchanged()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);
        var branch = await CreateBranchAsync(client, "Powai");

        var before = await factory.TargetMembershipAsync();

        var resp = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.TargetUserId}/scope",
            new SetMemberScopeRequest(branch.BranchId, "Cardiology"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<SetMemberScopeResult>())!;
        Assert.Equal(branch.BranchId, result.BranchId);
        Assert.Equal("Cardiology", result.Department);

        var after = await factory.TargetMembershipAsync();
        Assert.Equal(before.Utr, after.Utr);            // same membership row
        Assert.Equal(before.RoleId, after.RoleId);      // role_id NEVER touched (no escalation surface)
        Assert.Equal(branch.BranchId, after.BranchId);  // only the scope columns moved
        Assert.Equal("Cardiology", after.Department);
    }

    [Fact]
    public async Task Owner_SetScope_DoesNotChangeEffectivePermissions()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);
        var branch = await CreateBranchAsync(client, "Chembur");

        var before = await factory.ResolvePermissionsAsync(factory.TargetUserId, factory.TenantId);

        var resp = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.TargetUserId}/scope",
            new SetMemberScopeRequest(branch.BranchId, "Radiology"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var after = await factory.ResolvePermissionsAsync(factory.TargetUserId, factory.TenantId);

        // The RBAC boundary is unmoved: scope is a display attribute, not a permission input.
        Assert.NotEmpty(before);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ScopedMember_SurfacesBranchAndDepartment_InUsersList()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);
        var branch = await CreateBranchAsync(client, "Ghatkopar");

        var set = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.TargetUserId}/scope",
            new SetMemberScopeRequest(branch.BranchId, "Orthopaedics"));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        var users = (await (await client.GetAsync($"/api/v1/tenants/{factory.TenantId}/users?take=200"))
            .Content.ReadFromJsonAsync<List<UserListItemDto>>())!;

        var target = users.Single(u => u.UserId == factory.TargetUserId);
        Assert.Equal(branch.BranchId, target.BranchId);
        Assert.Equal("Ghatkopar", target.BranchName);
        Assert.Equal("Orthopaedics", target.Department);
    }

    [Fact]
    public async Task Viewer_SetScope_Forbidden()
    {
        var viewer = await AuthedClientAsync(factory.ViewerEmail);   // tenant_viewer lacks tenant.users.update

        var resp = await viewer.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.TargetUserId}/scope",
            new SetMemberScopeRequest(null, "Nope"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private async Task<CreateBranchResult> CreateBranchAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/branches", new CreateBranchRequest(name, null));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<CreateBranchResult>())!;
    }

    private async Task<HttpClient> AuthedClientAsync(string email)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, BranchScopeWebAppFactory.Password, factory.TenantId));
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }
}
