using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Iam;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Proves the IAM (Roles &amp; permissions admin) endpoints behave as designed under RLS as
/// <c>docslot_app</c>: the matrix read model is shaped correctly and read-only for built-in roles; the
/// checkbox toggle round-trips through grant/revoke_permission_from_role; and the database guards surface
/// as the right HTTP status — the system-role lock and the platform-scope escalation rule both 403, while
/// duplicate copies grants but forces is_grantable=false for a non-super actor (the auditor finding).
/// </summary>
public sealed class IamAdminTests(IamAdminWebAppFactory factory) : IClassFixture<IamAdminWebAppFactory>
{
    private const string TenantPermissionKey = "docslot.booking.read";   // tenant-scoped; tenant_owner holds it w/ grant option
    private const string PlatformPermissionKey = "platform.tenants.read"; // platform-scoped; a non-super may never confer it

    // ---- Reads ------------------------------------------------------------------------------------

    [Fact]
    public async Task Owner_ListModules_ReturnsCatalog()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);

        var resp = await client.GetAsync("/api/v1/iam/modules");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var modules = await resp.Content.ReadFromJsonAsync<List<ModuleDto>>();
        Assert.NotNull(modules);
        Assert.NotEmpty(modules!);
    }

    [Fact]
    public async Task Owner_GetBuiltinRoleMatrix_IsReadOnlyAndPopulated()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);
        var ownerRoleId = await IamAdminWebAppFactory.SystemRoleIdAsync("tenant_owner");

        var matrix = await GetMatrixAsync(client, ownerRoleId);

        Assert.True(matrix.IsSystem);
        Assert.False(matrix.Editable);                 // built-in → read-only in the UI
        Assert.NotEmpty(matrix.Modules);
        Assert.True(matrix.GrantedCount > 0);
        Assert.True(matrix.TotalCount >= matrix.GrantedCount);
        // The module tallies must sum to the role-level tally (the grid invariant the screen renders).
        Assert.Equal(matrix.GrantedCount, matrix.Modules.Sum(m => m.GrantedCount));
    }

    [Fact]
    public async Task GetMatrix_UnknownRole_Returns404()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);

        var resp = await client.GetAsync($"/api/v1/iam/roles/{Guid.NewGuid()}/matrix");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Owner_EffectiveAccess_ReturnsResolvedSet()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);

        var resp = await client.GetAsync(
            $"/api/v1/iam/users/{factory.OwnerUserId}/effective-access?tenantId={factory.TenantId}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<EffectiveAccessDto>();
        Assert.NotNull(dto);
        Assert.NotEmpty(dto!.PermissionKeys);
        Assert.Contains("tenant.roles.assign", dto.PermissionKeys);   // tenant_owner resolves this
    }

    // ---- Toggle round-trip on a custom role -------------------------------------------------------

    [Fact]
    public async Task Owner_ToggleCustomRoleCell_GrantThenRevoke_RoundTrips()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);
        var permId = await IamAdminWebAppFactory.PermissionIdAsync(TenantPermissionKey);

        // Grant (checkbox ON) — owner holds the perm with grant option, role is custom → succeeds.
        var grant = await client.PostAsJsonAsync(
            $"/api/v1/iam/roles/{factory.CustomRoleId}/permissions/{permId}",
            new SetRolePermissionRequest(factory.TenantId, Grantable: false));
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        var granted = (await grant.Content.ReadFromJsonAsync<SetRolePermissionResult>())!;
        Assert.True(granted.Granted);
        Assert.True(await IamAdminWebAppFactory.RoleHasPermissionAsync(factory.CustomRoleId, permId));

        // The matrix now reflects the grant on that exact cell.
        var afterGrant = await GetMatrixAsync(client, factory.CustomRoleId);
        Assert.True(CellFor(afterGrant, TenantPermissionKey).Granted);

        // Revoke (checkbox OFF) → removed; idempotent semantics return Granted:false.
        var revoke = await client.DeleteAsync(
            $"/api/v1/iam/roles/{factory.CustomRoleId}/permissions/{permId}?tenantId={factory.TenantId}");
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var revoked = (await revoke.Content.ReadFromJsonAsync<SetRolePermissionResult>())!;
        Assert.False(revoked.Granted);
        Assert.False(await IamAdminWebAppFactory.RoleHasPermissionAsync(factory.CustomRoleId, permId));

        var afterRevoke = await GetMatrixAsync(client, factory.CustomRoleId);
        Assert.False(CellFor(afterRevoke, TenantPermissionKey).Granted);
    }

    // ---- Guards: system-role lock + platform-scope escalation -------------------------------------

    [Fact]
    public async Task Owner_EditsSystemRoleMatrix_Gets403()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);
        var viewerRoleId = await IamAdminWebAppFactory.SystemRoleIdAsync("tenant_viewer");
        var permId = await IamAdminWebAppFactory.PermissionIdAsync(TenantPermissionKey);

        // A non-super actor may not edit a built-in role's matrix — the system-role guard fires (403).
        var resp = await client.DeleteAsync(
            $"/api/v1/iam/roles/{viewerRoleId}/permissions/{permId}?tenantId={factory.TenantId}");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Owner_GrantsPlatformScopedPermission_Gets403()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);
        var platformPermId = await IamAdminWebAppFactory.PermissionIdAsync(PlatformPermissionKey);

        // tenant_owner passes the API gate (tenant.roles.assign) but the DB refuses to let a non-super
        // confer platform-scoped authority → 403 (escalation guard).
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/iam/roles/{factory.CustomRoleId}/permissions/{platformPermId}",
            new SetRolePermissionRequest(factory.TenantId, Grantable: false));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.False(await IamAdminWebAppFactory.RoleHasPermissionAsync(factory.CustomRoleId, platformPermId));
    }

    // ---- Duplicate: gate + no-grant-option escalation ---------------------------------------------

    [Fact]
    public async Task Viewer_DuplicatesRole_Gets403_AtGate()
    {
        var client = await AuthedClientAsync(factory.ViewerEmail);
        var ownerRoleId = await IamAdminWebAppFactory.SystemRoleIdAsync("tenant_owner");

        // tenant_viewer lacks tenant.roles.assign → blocked by the RequirePermission gate before the DB.
        var resp = await client.PostAsJsonAsync("/api/v1/iam/roles/duplicate",
            new DuplicateRoleRequest(ownerRoleId, $"{factory.DuplicateKeyPrefix}_viewer", "Should Fail", null, factory.TenantId));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Owner_DuplicatesBuiltin_Succeeds_AndCopiesAreNonGrantable()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);
        var ownerRoleId = await IamAdminWebAppFactory.SystemRoleIdAsync("tenant_owner");

        var resp = await client.PostAsJsonAsync("/api/v1/iam/roles/duplicate",
            new DuplicateRoleRequest(ownerRoleId, $"{factory.DuplicateKeyPrefix}_ok", "IAM Dup OK", "test", factory.TenantId));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dup = (await resp.Content.ReadFromJsonAsync<DuplicateRoleResult>())!;
        Assert.NotEqual(Guid.Empty, dup.RoleId);

        // The clone carries the source's grants, but a non-super actor can never mint a grant-option
        // source: every copied grant must be is_grantable=false (the security-auditor finding).
        var (grantable, nonGrantable) = await IamAdminWebAppFactory.GrantOptionSplitAsync(dup.RoleId);
        Assert.True(nonGrantable > 0);
        Assert.Equal(0, grantable);

        // And it's a real, editable custom role: its matrix is non-system + editable.
        var matrix = await GetMatrixAsync(client, dup.RoleId);
        Assert.False(matrix.IsSystem);
        Assert.True(matrix.Editable);
    }

    // ---- Catalog plane: create modules + permissions ----------------------------------------------

    [Fact]
    public async Task Super_CreatesModule_ThenPermission_AppearsInMatrix()
    {
        var client = await AuthedClientAsync(factory.SuperAdminEmail);
        var moduleKey = $"{factory.CatalogPrefix}mod";
        var permKey = $"{moduleKey}.review";

        var modResp = await client.PostAsJsonAsync("/api/v1/iam/modules",
            new CreateModuleRequest(moduleKey, "IAM Catalog Module", "test", 900));
        Assert.Equal(HttpStatusCode.Created, modResp.StatusCode);
        Assert.NotEqual(Guid.Empty, (await modResp.Content.ReadFromJsonAsync<CreateModuleResult>())!.ResourceTypeId);

        var permResp = await client.PostAsJsonAsync("/api/v1/iam/permissions",
            new CreatePermissionRequest(permKey, moduleKey, "review", "tenant", "Review catalog things", IsDangerous: true));
        Assert.Equal(HttpStatusCode.Created, permResp.StatusCode);
        Assert.NotEqual(Guid.Empty, (await permResp.Content.ReadFromJsonAsync<CreatePermissionResult>())!.PermissionId);

        // The brand-new permission is immediately visible in a tenant role's matrix, under its module,
        // ungranted and carrying the dangerous flag — i.e. ready to be mapped to roles via the toggle.
        var matrix = await GetMatrixAsync(client, factory.CustomRoleId);
        var module = matrix.Modules.SingleOrDefault(m => m.ResourceKey == moduleKey);
        Assert.NotNull(module);
        var cell = module!.Cells.Single(c => c.PermissionKey == permKey);
        Assert.False(cell.Granted);
        Assert.True(cell.IsDangerous);
    }

    [Fact]
    public async Task Super_CreatesDuplicateModuleKey_Gets409()
    {
        var client = await AuthedClientAsync(factory.SuperAdminEmail);
        var moduleKey = $"{factory.CatalogPrefix}dup";

        var first = await client.PostAsJsonAsync("/api/v1/iam/modules",
            new CreateModuleRequest(moduleKey, "First", null, 0));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/iam/modules",
            new CreateModuleRequest(moduleKey, "Second", null, 0));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Owner_CreatesModule_Gets403_LacksPlatformPermissionsManage()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);

        // tenant_owner does not hold platform.permissions.manage → blocked at the RequirePermission gate.
        var resp = await client.PostAsJsonAsync("/api/v1/iam/modules",
            new CreateModuleRequest($"{factory.CatalogPrefix}nope", "Should Fail", null, 0));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- User list carries each user's roles (the row chips) -------------------------------------

    [Fact]
    public async Task ListTenantUsers_IncludesEachUsersRoles()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);

        var resp = await client.GetAsync($"/api/v1/tenants/{factory.TenantId}/users");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var users = (await resp.Content.ReadFromJsonAsync<List<UserListItemDto>>())!;

        // The owner row carries its tenant role (the join the Users tab renders as chips), with the
        // assignment id + primary flag — not an empty array.
        var owner = users.Single(u => u.UserId == factory.OwnerUserId);
        Assert.Contains(owner.Roles, r => r.RoleKey == "tenant_owner");
        Assert.All(owner.Roles, r => Assert.NotEqual(Guid.Empty, r.UserTenantRoleId));
        Assert.Contains(owner.Roles, r => r.IsPrimary);
    }

    // ---- Module licensing (commercial DISPLAY gate — must never change access) -------------------

    [Fact]
    public async Task Super_SetsModuleUnlicensed_MatrixGreysCell_ButGrantUnchanged()
    {
        var client = await AuthedClientAsync(factory.SuperAdminEmail);
        var permId = await IamAdminWebAppFactory.PermissionIdAsync(TenantPermissionKey);   // docslot.booking.read
        var bookingModuleId = await IamAdminWebAppFactory.ResourceTypeIdAsync("booking");

        // Arrange: grant booking.read on the custom role so we have a granted cell to observe.
        var grant = await client.PostAsJsonAsync(
            $"/api/v1/iam/roles/{factory.CustomRoleId}/permissions/{permId}",
            new SetRolePermissionRequest(factory.TenantId, Grantable: false));
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);

        // Default: the booking module is licensed.
        var before = await GetMatrixAsync(client, factory.CustomRoleId);
        Assert.True(before.Modules.Single(m => m.ResourceKey == "booking").Licensed);

        // Act: mark the booking module unlicensed for the tenant.
        var setResp = await client.PutAsJsonAsync($"/api/v1/iam/modules/{bookingModuleId}/license",
            new SetModuleLicenseRequest(factory.TenantId, IsLicensed: false, Reason: "not on plan"));
        Assert.Equal(HttpStatusCode.OK, setResp.StatusCode);

        // Assert: the matrix greys the module (licensed=false, cells moduleLicensed=false) — but the GRANT
        // is untouched. Licensing is a display gate; it never revokes the permission.
        var after = await GetMatrixAsync(client, factory.CustomRoleId);
        var module = after.Modules.Single(m => m.ResourceKey == "booking");
        Assert.False(module.Licensed);
        var cell = module.Cells.Single(c => c.PermissionKey == TenantPermissionKey);
        Assert.False(cell.ModuleLicensed);
        Assert.True(cell.Granted);   // <-- the load-bearing assertion: still granted

        // Restore shared fixture state (tests in a class share the tenant): re-license + drop the grant.
        await client.PutAsJsonAsync($"/api/v1/iam/modules/{bookingModuleId}/license",
            new SetModuleLicenseRequest(factory.TenantId, IsLicensed: true, Reason: "test restore"));
        await client.DeleteAsync(
            $"/api/v1/iam/roles/{factory.CustomRoleId}/permissions/{permId}?tenantId={factory.TenantId}");
    }

    [Fact]
    public async Task UnlicensedModule_DoesNotChange_EffectiveAccess()
    {
        var client = await AuthedClientAsync(factory.SuperAdminEmail);
        var bookingModuleId = await IamAdminWebAppFactory.ResourceTypeIdAsync("booking");

        // Mark booking unlicensed for the tenant…
        var setResp = await client.PutAsJsonAsync($"/api/v1/iam/modules/{bookingModuleId}/license",
            new SetModuleLicenseRequest(factory.TenantId, IsLicensed: false, Reason: "display-only proof"));
        Assert.Equal(HttpStatusCode.OK, setResp.StatusCode);

        // …the owner's RESOLVED permission set is unchanged — booking.read still present. Licensing is a
        // display gate and resolve_user_permissions never consults it. This is the security invariant.
        var resp = await client.GetAsync(
            $"/api/v1/iam/users/{factory.OwnerUserId}/effective-access?tenantId={factory.TenantId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = (await resp.Content.ReadFromJsonAsync<EffectiveAccessDto>())!;
        Assert.Contains(TenantPermissionKey, dto.PermissionKeys);

        // Restore shared fixture state.
        await client.PutAsJsonAsync($"/api/v1/iam/modules/{bookingModuleId}/license",
            new SetModuleLicenseRequest(factory.TenantId, IsLicensed: true, Reason: "test restore"));
    }

    [Fact]
    public async Task Owner_SetsModuleLicense_Gets403_LacksSettingsUpdate()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);
        var bookingModuleId = await IamAdminWebAppFactory.ResourceTypeIdAsync("booking");

        // tenant_owner does not hold platform.settings.update → blocked at the RequirePermission gate.
        var resp = await client.PutAsJsonAsync($"/api/v1/iam/modules/{bookingModuleId}/license",
            new SetModuleLicenseRequest(factory.TenantId, IsLicensed: false, Reason: "should fail"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private async Task<HttpClient> AuthedClientAsync(string email)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, IamAdminWebAppFactory.Password, factory.TenantId));
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private static async Task<RoleMatrixDto> GetMatrixAsync(HttpClient client, Guid roleId)
    {
        var resp = await client.GetAsync($"/api/v1/iam/roles/{roleId}/matrix");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<RoleMatrixDto>())!;
    }

    private static RoleMatrixCellDto CellFor(RoleMatrixDto matrix, string permissionKey)
        => matrix.Modules.SelectMany(m => m.Cells).Single(c => c.PermissionKey == permissionKey);
}
