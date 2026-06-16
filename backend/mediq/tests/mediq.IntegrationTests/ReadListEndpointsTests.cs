using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Commission;
using mediq.SharedDataModel.Docslot.PlatformApi;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice 08b — read-list GET endpoints backing the FE mocks. Verifies the three DoD invariants:
///   (1) a paginated, gated list returns the page shape for an authorized caller and DENIES (403) a caller
///       without the gating permission;
///   (2) a clinical list requires X-Purpose-Of-Use (422 without it);
///   (3) a list masks PHI (the broker/Care-Partner list never carries a raw phone).
/// </summary>
public sealed class ApiRequestLogEndpointTests(PlatformApiWebAppFactory factory) : IClassFixture<PlatformApiWebAppFactory>
{
    [Fact]
    public async Task ApiRequests_List_Is_Paginated_And_Returns_Page_Shape_For_Admin()
    {
        var client = factory.CreateClient();
        var token = await LoginAsync(client, factory.AdminEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var resp = await client.GetAsync("/api/v1/api-requests?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var page = await resp.Content.ReadFromJsonAsync<ApiRequestLogPageDto>();
        Assert.NotNull(page);
        Assert.Equal(1, page!.Page);
        Assert.Equal(10, page.PageSize);
        Assert.True(page.Total >= 0);
        // Items never exceed the page size, and never carry a body/IP — the DTO has no such field by design.
        Assert.True(page.Items.Count <= 10);
    }

    [Fact]
    public async Task ApiRequests_List_Is_Denied_Without_The_Manage_Permission()
    {
        // A fresh user with a zero-permission custom role must be 403'd by platform.api_clients.manage.
        var (email, userId, roleId) = await SeedZeroPermissionUserAsync();
        try
        {
            var client = factory.CreateClient();
            var token = await LoginAsync(client, email);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            var resp = await client.GetAsync("/api/v1/api-requests");
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally { await CleanupUserAsync(email, userId, roleId); }
    }

    private static async Task<TokenResponse> LoginAsync(HttpClient client, string email)
    {
        // The slice-02 admin is a platform-wide super_admin (tenant_id NULL) — login carries no tenant.
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, PlatformApiWebAppFactory.AdminPassword));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private static async Task<(string Email, Guid UserId, Guid RoleId)> SeedZeroPermissionUserAsync()
    {
        // A platform-scoped custom role with ZERO permissions → cannot resolve platform.api_clients.manage.
        var email = $"slice08b.deny+{Guid.NewGuid():N}@docslot.test";
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var roleKey = ("s08b_zero_" + Guid.NewGuid().ToString("N"))[..28];
        await using var conn = new Npgsql.NpgsqlConnection(PlatformApiWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await Exec(conn,
            "INSERT INTO platform.users (user_id,email,password_hash,full_name,is_active,is_platform_user,created_at,updated_at) VALUES (@id,@e,crypt(@p,gen_salt('bf',10)),'S08b Deny',true,true,NOW(),NOW()) ON CONFLICT (email) DO UPDATE SET password_hash=EXCLUDED.password_hash, deleted_at=NULL",
            ("id", userId), ("e", email), ("p", PlatformApiWebAppFactory.AdminPassword));
        await Exec(conn,
            "INSERT INTO platform.roles (role_id,role_key,name,scope,tenant_id,is_system,created_at,updated_at) VALUES (@r,@k,'S08b Zero','platform',NULL,false,NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("r", roleId), ("k", roleKey));
        await Exec(conn,
            "INSERT INTO platform.user_tenant_roles (user_tenant_role_id,user_id,tenant_id,role_id,is_primary,granted_at) VALUES (gen_random_uuid(),@u,NULL,@r,true,NOW()) ON CONFLICT DO NOTHING",
            ("u", userId), ("r", roleId));
        return (email, userId, roleId);
    }

    private static async Task CleanupUserAsync(string email, Guid userId, Guid roleId)
    {
        await using var conn = new Npgsql.NpgsqlConnection(PlatformApiWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await Try(conn, "DELETE FROM platform.user_sessions WHERE user_id=@u", ("u", userId));
        await Try(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id=@u", ("u", userId));
        await Try(conn, "DELETE FROM platform.login_attempts WHERE email=@e", ("e", email));
        await Try(conn, "DELETE FROM platform.roles WHERE role_id=@r", ("r", roleId));
        await Try(conn, "UPDATE platform.users SET deleted_at=NOW(), email=@anon, is_active=false WHERE user_id=@u",
            ("anon", $"deleted+{userId}@s08b.test"), ("u", userId));
    }

    private static async Task Exec(Npgsql.NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
    private static async Task Try(Npgsql.NpgsqlConnection conn, string sql, params (string Name, object Value)[] args)
    { try { await Exec(conn, sql, args); } catch { } }
}

/// <summary>Clinical list requires a declared purpose-of-use (DPDP). Reuses the slice-03b factory.</summary>
public sealed class ClinicalReadListTests(ClinicalWebAppFactory factory) : IClassFixture<ClinicalWebAppFactory>
{
    [Fact]
    public async Task LabReports_List_Without_Purpose_Header_Is_Rejected_422()
    {
        var client = factory.CreateClient();
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // No X-Purpose-Of-Use header → the clinical list must refuse (422), same gate as the detail read.
        var resp = await client.GetAsync($"/api/v1/patients/{factory.PatientId}/lab-reports");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task LabReports_List_With_Purpose_Returns_Ok_Headers_Only()
    {
        var client = factory.CreateClient();
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        client.DefaultRequestHeaders.Add("X-Purpose-Of-Use", "treatment");

        var resp = await client.GetAsync($"/api/v1/patients/{factory.PatientId}/lab-reports");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // The list parses (it is a header-only projection — no decrypted clinical body field exists on it).
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("structuredResults", body, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<TokenResponse> LoginAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.AdminEmail, ClinicalWebAppFactory.AdminPassword, factory.TenantA));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
    }
}

/// <summary>The Care-Partner (broker) list masks PHI — the raw phone is never serialised. Slice-07 factory.</summary>
public sealed class CommissionReadListTests(CommissionWebAppFactory factory) : IClassFixture<CommissionWebAppFactory>
{
    [Fact]
    public async Task Broker_List_Masks_Phone_And_Carries_No_Raw_Number()
    {
        var client = factory.CreateClient();
        var token = await LoginAsync(client, factory.SuperEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var resp = await client.GetAsync("/api/v1/commission/brokers?take=50");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var raw = await resp.Content.ReadAsStringAsync();
        // The seeded broker's raw phone must NOT appear anywhere in the list payload (DPDP).
        Assert.DoesNotContain(factory.BrokerPhone, raw);

        var brokers = await resp.Content.ReadFromJsonAsync<List<BrokerDto>>();
        Assert.NotNull(brokers);
        var seeded = brokers!.SingleOrDefault(b => b.BrokerId == factory.BrokerId);
        Assert.NotNull(seeded);
        // Masked phone is present, contains the mask char, and is not the raw number.
        Assert.Contains('x', seeded!.MaskedPhone);
        Assert.NotEqual(factory.BrokerPhone, seeded.MaskedPhone);
    }

    [Fact]
    public async Task Attribution_List_Is_Gated_And_Returns_Ok_For_Super()
    {
        var client = factory.CreateClient();
        var token = await LoginAsync(client, factory.SuperEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var resp = await client.GetAsync("/api/v1/commission/attributions?take=20");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var raw = await resp.Content.ReadAsStringAsync();
        // No PAN ever; the attribution list carries first-name + masked phone only.
        Assert.DoesNotContain("panNumber", raw, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<TokenResponse> LoginAsync(HttpClient client, string email)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, CommissionWebAppFactory.Password, factory.TenantId));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
    }
}
