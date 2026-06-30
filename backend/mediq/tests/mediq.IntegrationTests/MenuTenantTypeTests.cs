using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Navigation;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice-13 — GET /api/v1/me/menus is TENANT_TYPE-aware: the backend-driven nav tree varies by the active
/// tenant's tenant_type (platform.navigation_menus.applies_to_tenant_types), independent of permissions. The
/// same super_admin user (so the permission gate always passes) sees the 'doctors' screen in a 'hospital'
/// tenant but NOT in a 'pathology_lab' tenant, while 'lab' (scoped to both) and the unscoped screens appear in
/// both. This is the regression guard that was previously untested.
/// </summary>
public sealed class MenuTenantTypeTests(MenuTenantTypeWebAppFactory factory) : IClassFixture<MenuTenantTypeWebAppFactory>
{
    [Fact]
    public async Task Menus_Vary_By_Tenant_Type_DoctorsAbsentForLab_LabPresentForBoth()
    {
        // Hospital tenant: 'doctors' (applies hospital/clinic/diagnostic) AND 'lab' (applies lab/hospital/diagnostic) present.
        var hospital = await MenusForTenantAsync(factory.HospitalTenantId);
        var hospitalKeys = AllKeys(hospital);
        Assert.Contains("doctors", hospitalKeys);
        Assert.Contains("lab", hospitalKeys);
        Assert.Contains("dashboard", hospitalKeys);   // unscoped (applies_to_tenant_types NULL) → every type
        Assert.Contains("ai_ops", hospitalKeys);       // slice-15 AI Operations nav row (gated on the clinical reads the super_admin holds)

        // Pathology-lab tenant: 'doctors' is NOT in the lab's tenant_type set → ABSENT; 'lab' + unscoped present.
        var lab = await MenusForTenantAsync(factory.LabTenantId);
        var labKeys = AllKeys(lab);
        Assert.DoesNotContain("doctors", labKeys);     // the tenant_type discriminator
        Assert.Contains("lab", labKeys);               // scoped to pathology_lab → present
        Assert.Contains("dashboard", labKeys);         // unscoped → present
        Assert.Contains("team", labKeys);              // unscoped admin screen → present
        Assert.Contains("ai_ops", labKeys);            // slice-15 AI Operations nav row (unscoped + perm-gated)

        // Same user, same permissions (super_admin) — so the ONLY thing that removed 'doctors' was tenant_type.
        // Sanity: the lab tree is a strict subset on this discriminator (hospital had 'doctors', lab does not).
        Assert.True(hospitalKeys.Contains("doctors") && !labKeys.Contains("doctors"));
    }

    [Fact]
    public async Task Menus_Are_Bilingual_And_Tree_Is_Well_Formed_Under_Tenant_Type_Filtering()
    {
        var lab = await MenusForTenantAsync(factory.LabTenantId);

        // Bilingual contract: every returned node carries a Hindi label (hi parity), even under filtering.
        foreach (var node in Flatten(lab))
            Assert.False(string.IsNullOrWhiteSpace(node.LabelHi), $"menu '{node.Key}' is missing a Hindi label");

        // Tree integrity: filtering out a sibling top-level screen ('doctors') must NOT orphan any surviving
        // child to the root — every child's ParentId equals its real parent's Id (no broken hierarchy).
        AssertParentLinksConsistent(lab, parentId: null);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<IReadOnlyList<MenuNodeDto>> MenusForTenantAsync(Guid tenantId)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.Email, MenuTenantTypeWebAppFactory.Password, tenantId));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var resp = await client.GetAsync("/api/v1/me/menus");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<List<MenuNodeDto>>())!;
    }

    private static IEnumerable<MenuNodeDto> Flatten(IEnumerable<MenuNodeDto> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    private static HashSet<string> AllKeys(IEnumerable<MenuNodeDto> nodes) =>
        Flatten(nodes).Select(n => n.Key).ToHashSet();

    private static void AssertParentLinksConsistent(IEnumerable<MenuNodeDto> nodes, Guid? parentId)
    {
        foreach (var n in nodes)
        {
            Assert.Equal(parentId, n.ParentId);   // a child surfaced only under its real parent (no orphan-to-root)
            AssertParentLinksConsistent(n.Children, n.Id);
        }
    }
}
