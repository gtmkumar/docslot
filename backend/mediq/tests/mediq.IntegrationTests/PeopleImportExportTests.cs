using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.SharedDataModel.Docslot.Auth;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Proves the People export + bulk-import surface (issue #95) under RLS as <c>docslot_app</c>:
///   • export is tenant-scoped (a 2nd tenant's members are absent), CSV-injection-safe (a name starting with
///     <c>=</c> is neutralised), and gated on <c>tenant.users.read</c>;
///   • bulk-import creates N users, confers roles subject to the R3 no-escalation guard (a row requesting a
///     non-conferrable role is rejected for THAT row while valid rows still succeed), links an existing email
///     rather than overwriting it, is gated on <c>tenant.users.create</c>, and rejects an oversize batch.
/// </summary>
public sealed class PeopleImportExportTests(PeopleImportExportWebAppFactory factory)
    : IClassFixture<PeopleImportExportWebAppFactory>
{
    // ---- Export ----------------------------------------------------------------------------------

    [Fact]
    public async Task Export_ReturnsOnlyOwnTenantMembers_AndNeutralisesFormulaInjection()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);

        var resp = await client.GetAsync($"/api/v1/tenants/{factory.TenantId}/users/export");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/csv", resp.Content.Headers.ContentType!.MediaType);
        var csv = await resp.Content.ReadAsStringAsync();

        // Own-tenant members are present.
        Assert.Contains(factory.OwnerEmail, csv);
        Assert.Contains(factory.NoAccessEmail, csv);

        // A 2nd tenant's member must NOT leak into this tenant's export.
        Assert.DoesNotContain(factory.TenantBOwnerEmail, csv);

        // CSV-injection-safe: the '=SUM(...)' name is neutralised with a leading apostrophe, never emitted raw
        // as the first cell of a line (which spreadsheets would evaluate as a formula).
        Assert.Contains("'" + factory.InjectionFullName, csv);
        Assert.DoesNotContain("\n" + factory.InjectionFullName, csv);
    }

    [Fact]
    public async Task Export_WithoutUsersRead_Gets403()
    {
        // The no-access member holds an empty role → resolves to zero perms → blocked at the RequirePermission gate.
        var client = await AuthedClientAsync(factory.NoAccessEmail);

        var resp = await client.GetAsync($"/api/v1/tenants/{factory.TenantId}/users/export");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- Bulk import -----------------------------------------------------------------------------

    [Fact]
    public async Task BulkImport_CreatesUsers_AssignsRoles_ReturnsPerRowResults()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);

        var emailA = $"{factory.ImportPrefix}.a@docslot.test";
        var emailB = $"{factory.ImportPrefix}.b@docslot.test";
        var rows = new[]
        {
            new BulkImportUserRow(emailA, "Import A", factory.ConferRoleKey),   // conferrable role
            new BulkImportUserRow(emailB, "Import B", null),                    // no role — just provisioned
        };

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/bulk-import", new BulkImportUsersRequest(rows));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<BulkImportResult>())!;

        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Created);
        Assert.Equal(0, result.Errored);
        Assert.All(result.Rows, r => Assert.Equal("created", r.Status));

        // Both users now exist; row A carries an ACTIVE assignment of the conferrable role.
        Assert.True(await PeopleImportExportWebAppFactory.LiveUserExistsAsync(emailA));
        Assert.True(await PeopleImportExportWebAppFactory.LiveUserExistsAsync(emailB));
        Assert.Equal(1, await PeopleImportExportWebAppFactory.ActiveRoleAssignmentCountAsync(
            emailA, factory.ConferRoleId, factory.TenantId));
    }

    [Fact]
    public async Task BulkImport_NonConferrableRole_RejectsThatRow_ButValidRowsSucceed()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);

        var badEmail = $"{factory.ImportPrefix}.escalate@docslot.test";
        var goodEmail = $"{factory.ImportPrefix}.good@docslot.test";
        var rows = new[]
        {
            // super_admin is a platform role a tenant_owner may NOT confer — the R3 escalation guard fires.
            new BulkImportUserRow(badEmail, "Escalation Attempt", "super_admin"),
            new BulkImportUserRow(goodEmail, "Valid Row", factory.ConferRoleKey),
        };

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/bulk-import", new BulkImportUsersRequest(rows));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<BulkImportResult>())!;

        // The escalating row is an error; the valid row still succeeded (batch not aborted).
        var bad = result.Rows.Single(r => r.Email == badEmail);
        Assert.Equal("error", bad.Status);
        var good = result.Rows.Single(r => r.Email == goodEmail);
        Assert.Equal("created", good.Status);
        Assert.Equal(1, result.Errored);
        Assert.Equal(1, result.Created);

        // Per-row atomicity: the escalating row rolled back entirely — no orphan user was minted for it.
        Assert.False(await PeopleImportExportWebAppFactory.LiveUserExistsAsync(badEmail));
        // The valid row committed, with its role conferred.
        Assert.True(await PeopleImportExportWebAppFactory.LiveUserExistsAsync(goodEmail));
        Assert.Equal(1, await PeopleImportExportWebAppFactory.ActiveRoleAssignmentCountAsync(
            goodEmail, factory.ConferRoleId, factory.TenantId));
    }

    [Fact]
    public async Task BulkImport_ExistingEmail_Links_DoesNotOverwrite()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);

        // Pre-seed an existing global identity (swept by the import prefix at teardown).
        var email = $"{factory.ImportPrefix}.existing@docslot.test";
        await SeedUserAsync(email, "Original Name");

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/bulk-import",
            new BulkImportUsersRequest(new[] { new BulkImportUserRow(email, "New Name Attempt", null) }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<BulkImportResult>())!;

        var row = Assert.Single(result.Rows);
        Assert.Equal("linked", row.Status);
        Assert.Equal(1, result.Linked);
        Assert.Equal(0, result.Created);

        // The existing profile is NOT overwritten (link, don't recreate) — full_name is unchanged.
        Assert.Equal("Original Name", await FullNameAsync(email));
    }

    [Fact]
    public async Task BulkImport_WithoutUsersCreate_Gets403()
    {
        // The no-access member lacks tenant.users.create → blocked at the RequirePermission gate.
        var client = await AuthedClientAsync(factory.NoAccessEmail);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/bulk-import",
            new BulkImportUsersRequest(new[] { new BulkImportUserRow("x@docslot.test", "X", null) }));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task BulkImport_OversizeBatch_Gets422()
    {
        var client = await AuthedClientAsync(factory.OwnerEmail);

        // 501 rows > the 500 cap → rejected wholesale (never partially processed).
        var rows = Enumerable.Range(0, 501)
            .Select(i => new BulkImportUserRow($"{factory.ImportPrefix}.over{i}@docslot.test", $"Over {i}", null))
            .ToArray();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/bulk-import", new BulkImportUsersRequest(rows));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        // None of the oversize rows were created.
        Assert.False(await PeopleImportExportWebAppFactory.LiveUserExistsAsync($"{factory.ImportPrefix}.over0@docslot.test"));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static Task SeedUserAsync(string email, string fullName) =>
        ExecAsync(
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, created_at, updated_at)
            VALUES (gen_random_uuid(), @email, crypt('Sup3rSecret!', gen_salt('bf', 10)), @fn, true, true, false, NOW(), NOW())
            ON CONFLICT (email) DO UPDATE SET deleted_at = NULL
            """,
            ("email", email), ("fn", fullName));

    private static async Task<string> FullNameAsync(string email)
    {
        await using var conn = new NpgsqlConnection(PeopleImportExportWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT full_name FROM platform.users WHERE email = @e", conn);
        cmd.Parameters.AddWithValue("e", email);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecAsync(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(PeopleImportExportWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<HttpClient> AuthedClientAsync(string email)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, PeopleImportExportWebAppFactory.Password, factory.TenantId));
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }
}
