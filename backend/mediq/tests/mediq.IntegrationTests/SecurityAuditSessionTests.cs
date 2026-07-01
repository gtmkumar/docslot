using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Security;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Backend for epic #80 Phase B:
///   #86 — the READ side of the WRITE-only <c>platform.audit_log</c> (Audit tab): tenant-scoped, faceted,
///         gated by <c>tenant.audit.read</c>, with a CSV export.
///   #87 — active-session oversight + admin revoke, tenant-scoped, gated by <c>tenant.users.update</c>.
/// </summary>
public sealed class SecurityAuditSessionTests(SecurityAuditSessionWebAppFactory factory)
    : IClassFixture<SecurityAuditSessionWebAppFactory>
{
    // ---- #86 Audit read -------------------------------------------------------------------------

    [Fact]
    public async Task Audit_Read_Returns_Own_Tenant_Rows_With_Category_And_Severity_And_Facets()
    {
        var client = factory.CreateClient();
        await AuthAsync(client, factory.OwnerEmail, factory.TenantA);

        // Wide search on the unique marker isolates exactly this fixture's four Tenant-A rows.
        var resp = await client.GetAsync($"/api/v1/security/audit/logs?pageSize=200&search={factory.AuditMarker}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var page = await resp.Content.ReadFromJsonAsync<AuditLogPageDto>();
        Assert.NotNull(page);
        Assert.Equal(4, page!.Total);
        Assert.Equal(4, page.Items.Count);

        // Category derivation: booking→Bookings, patient/prescription→Patients, tenant_settings→Settings.
        var booking = page.Items.Single(i => i.RawAction == "delete" && i.ResourceType == "booking");
        Assert.Equal(AuditTaxonomy.Bookings, booking.Category);
        Assert.Equal(AuditTaxonomy.Critical, booking.Severity);       // dangerous (delete) + failed → Critical
        Assert.False(booking.Success);
        Assert.Equal("Delete", booking.Action);                       // humanized

        var view = page.Items.Single(i => i.RawAction == "view" && i.ResourceType == "patient");
        Assert.Equal(AuditTaxonomy.Patients, view.Category);
        Assert.Equal(AuditTaxonomy.Informational, view.Severity);     // ordinary success → Informational

        var glass = page.Items.Single(i => i.RawAction == "break_glass");
        Assert.Equal(AuditTaxonomy.Warning, glass.Severity);          // dangerous + success → Warning
        Assert.Equal("Break Glass", glass.Action);

        // Actor identity IS included as distinct fields (the FE decides masking).
        Assert.All(page.Items, i => Assert.Equal(factory.OwnerUserId, i.ActorUserId));
        Assert.All(page.Items, i => Assert.False(string.IsNullOrEmpty(i.ActorEmail)));

        // Facets over the filtered set.
        Assert.Equal(2, page.CategoryFacets.Single(f => f.Key == AuditTaxonomy.Patients).Count);
        Assert.Equal(1, page.CategoryFacets.Single(f => f.Key == AuditTaxonomy.Bookings).Count);
        Assert.Equal(1, page.SeverityFacets.Single(f => f.Key == AuditTaxonomy.Critical).Count);
        Assert.Equal(2, page.SeverityFacets.Single(f => f.Key == AuditTaxonomy.Informational).Count);

        // No hash-chain internals / PHI body ever leak.
        var raw = await (await client.GetAsync($"/api/v1/security/audit/logs?search={factory.AuditMarker}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("beforeData", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chainHash", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Audit_Read_Category_Filter_Narrows_To_The_Bucket()
    {
        var client = factory.CreateClient();
        await AuthAsync(client, factory.OwnerEmail, factory.TenantA);

        var resp = await client.GetAsync(
            $"/api/v1/security/audit/logs?pageSize=200&search={factory.AuditMarker}&category={AuditTaxonomy.Patients}");
        var page = await resp.Content.ReadFromJsonAsync<AuditLogPageDto>();
        Assert.NotNull(page);
        Assert.Equal(2, page!.Total);   // patient view + prescription break_glass
        Assert.All(page.Items, i => Assert.Equal(AuditTaxonomy.Patients, i.Category));
    }

    [Fact]
    public async Task Audit_Read_Never_Returns_Another_Tenants_Rows()
    {
        var client = factory.CreateClient();
        await AuthAsync(client, factory.OwnerEmail, factory.TenantA);

        // Search on the Tenant-B marker while authenticated to Tenant A → zero rows (explicit tenant predicate).
        var resp = await client.GetAsync($"/api/v1/security/audit/logs?pageSize=200&search={factory.ForeignAuditMarker}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var page = await resp.Content.ReadFromJsonAsync<AuditLogPageDto>();
        Assert.NotNull(page);
        Assert.Equal(0, page!.Total);

        var raw = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(factory.ForeignAuditMarker, raw);
    }

    [Fact]
    public async Task Audit_Read_Is_Denied_Without_Tenant_Audit_Read()
    {
        var client = factory.CreateClient();
        await AuthAsync(client, factory.ZeroEmail, factory.TenantA);

        var resp = await client.GetAsync("/api/v1/security/audit/logs");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Audit_Csv_Export_Has_Header_And_The_Seeded_Rows()
    {
        var client = factory.CreateClient();
        await AuthAsync(client, factory.OwnerEmail, factory.TenantA);

        var resp = await client.GetAsync($"/api/v1/security/audit/logs/export?search={factory.AuditMarker}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/csv", resp.Content.Headers.ContentType?.MediaType);

        var csv = await resp.Content.ReadAsStringAsync();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("occurred_at,category,severity,action", lines[0]);
        Assert.Equal(5, lines.Length);   // header + four Tenant-A rows
        Assert.Contains("Bookings", csv);
        Assert.Contains("Critical", csv);
        // Never any hash-chain / PHI body column.
        Assert.DoesNotContain("before_data", csv);
        Assert.DoesNotContain("chain", csv, StringComparison.OrdinalIgnoreCase);
    }

    // ---- #87 Sessions ---------------------------------------------------------------------------

    [Fact]
    public async Task Sessions_List_Returns_Only_Current_Tenant_Members()
    {
        var client = factory.CreateClient();
        await AuthAsync(client, factory.OwnerEmail, factory.TenantA);

        var resp = await client.GetAsync("/api/v1/security/sessions?take=500");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var sessions = await resp.Content.ReadFromJsonAsync<List<ActiveSessionDto>>();
        Assert.NotNull(sessions);

        // The member's seeded session is present…
        Assert.Contains(sessions!, s => s.SessionId == factory.MemberSessionListId && s.UserId == factory.MemberUserId);
        // …the foreign (Tenant-B) user's session is NOT — never lists a non-member.
        Assert.DoesNotContain(sessions!, s => s.SessionId == factory.ForeignSessionId);
        Assert.DoesNotContain(sessions!, s => s.UserId == factory.ForeignUserId);

        // No token material of any kind is serialised.
        var raw = await (await client.GetAsync("/api/v1/security/sessions?take=500")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("tokenHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Session_Revoke_Sets_Revoked_And_Drops_From_The_List()
    {
        var client = factory.CreateClient();
        await AuthAsync(client, factory.OwnerEmail, factory.TenantA);

        Assert.True(await SecurityAuditSessionWebAppFactory.SessionIsActiveAsync(factory.MemberSessionRevokeId));

        var resp = await client.PostAsync($"/api/v1/security/sessions/{factory.MemberSessionRevokeId}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(await resp.Content.ReadFromJsonAsync<bool>());

        // Revoked at the DB…
        Assert.False(await SecurityAuditSessionWebAppFactory.SessionIsActiveAsync(factory.MemberSessionRevokeId));
        // …and no longer in the active list.
        var list = await (await client.GetAsync("/api/v1/security/sessions?take=500"))
            .Content.ReadFromJsonAsync<List<ActiveSessionDto>>();
        Assert.DoesNotContain(list!, s => s.SessionId == factory.MemberSessionRevokeId);
    }

    [Fact]
    public async Task Session_Revoke_Of_A_Non_Member_Is_Refused()
    {
        var client = factory.CreateClient();
        await AuthAsync(client, factory.OwnerEmail, factory.TenantA);

        // The foreign session belongs to a Tenant-B user → refused (404), and it stays active.
        var resp = await client.PostAsync($"/api/v1/security/sessions/{factory.ForeignSessionId}/revoke", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.True(await SecurityAuditSessionWebAppFactory.SessionIsActiveAsync(factory.ForeignSessionId));
    }

    [Fact]
    public async Task Sessions_List_And_Revoke_Exclude_A_Members_Other_Tenant_Session()
    {
        // #87 hardening (audit MEDIUM): MemberUser IS a Tenant-A member, but this session was ESTABLISHED
        // under Tenant B (active_tenant_id = TenantB). A Tenant-A admin must NOT see or revoke it — owner
        // membership alone is insufficient; the session's active_tenant_id gates it.
        var client = factory.CreateClient();
        await AuthAsync(client, factory.OwnerEmail, factory.TenantA);

        var list = await (await client.GetAsync("/api/v1/security/sessions?take=500"))
            .Content.ReadFromJsonAsync<List<ActiveSessionDto>>();
        Assert.DoesNotContain(list!, s => s.SessionId == factory.MemberOtherTenantSessionId);

        var revoke = await client.PostAsync($"/api/v1/security/sessions/{factory.MemberOtherTenantSessionId}/revoke", null);
        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);

        // control: the refused single-revoke left it live.
        Assert.True(await SecurityAuditSessionWebAppFactory.SessionIsActiveAsync(factory.MemberOtherTenantSessionId));
    }

    [Fact]
    public async Task Session_Revoke_All_Of_A_Non_Member_Is_Forbidden()
    {
        var client = factory.CreateClient();
        await AuthAsync(client, factory.OwnerEmail, factory.TenantA);

        var resp = await client.PostAsync($"/api/v1/security/sessions/users/{factory.ForeignUserId}/revoke-all", null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.True(await SecurityAuditSessionWebAppFactory.SessionIsActiveAsync(factory.ForeignSessionId));
    }

    [Fact]
    public async Task Sessions_List_Is_Denied_Without_Tenant_Users_Update()
    {
        var client = factory.CreateClient();
        // Viewer holds tenant.audit.read but LACKS tenant.users.update → 403 on the sessions surface.
        await AuthAsync(client, factory.ViewerEmail, factory.TenantA);

        var resp = await client.GetAsync("/api/v1/security/sessions");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- #94 geo-IP resolver seam ---------------------------------------------------------------

    [Fact]
    public async Task NullGeoIpResolver_ReturnsNullCity_ForAnyIp()
    {
        // The offline default performs NO external lookup — every IP resolves to null (unknown).
        var resolver = new mediq.Infrastructure.Security.NullGeoIpResolver();
        Assert.Null(await resolver.ResolveCityAsync("203.0.113.7", default));
        Assert.Null(await resolver.ResolveCityAsync(null, default));
    }

    [Fact]
    public async Task Sessions_And_Audit_Surface_Null_City_When_Resolver_Is_Unknown()
    {
        factory.Geo.City = null;   // offline / unknown — the seam returns no city
        var client = factory.CreateClient();
        await AuthAsync(client, factory.OwnerEmail, factory.TenantA);

        var sessions = await (await client.GetAsync("/api/v1/security/sessions?take=500"))
            .Content.ReadFromJsonAsync<List<ActiveSessionDto>>();
        var member = Assert.Single(sessions!, s => s.SessionId == factory.MemberSessionListId);
        Assert.NotNull(member.IpAddress);   // the IP is still shown…
        Assert.Null(member.City);           // …but the city is null (UI shows just the IP)

        var page = await (await client.GetAsync($"/api/v1/security/audit/logs?pageSize=200&search={factory.AuditMarker}"))
            .Content.ReadFromJsonAsync<AuditLogPageDto>();
        Assert.All(page!.Items, i => Assert.Null(i.City));
    }

    [Fact]
    public async Task Sessions_And_Audit_Surface_Resolved_City_When_Provider_Returns_One()
    {
        factory.Geo.City = "Kolkata";   // a live provider would fill this in
        try
        {
            var client = factory.CreateClient();
            await AuthAsync(client, factory.OwnerEmail, factory.TenantA);

            var sessions = await (await client.GetAsync("/api/v1/security/sessions?take=500"))
                .Content.ReadFromJsonAsync<List<ActiveSessionDto>>();
            var member = Assert.Single(sessions!, s => s.SessionId == factory.MemberSessionListId);
            Assert.Equal("Kolkata", member.City);

            // The audit rows (which carry the seeded 203.0.113.x-style IP) surface the same resolved city.
            var page = await (await client.GetAsync($"/api/v1/security/audit/logs?pageSize=200&search={factory.AuditMarker}"))
                .Content.ReadFromJsonAsync<AuditLogPageDto>();
            Assert.NotEmpty(page!.Items);
            Assert.All(page.Items.Where(i => i.IpAddress is not null), i => Assert.Equal("Kolkata", i.City));
        }
        finally
        {
            factory.Geo.City = null;
        }
    }

    private static async Task AuthAsync(HttpClient client, string email, Guid tenantId)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, SecurityAuditSessionWebAppFactory.Password, tenantId));
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }
}
