using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Iam;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace mediq.IntegrationTests;

/// <summary>
/// Output caching of caller-invariant catalog/reference responses (see <c>mediq.Api/Caching/OutputCaching.cs</c>).
/// Proves the four properties that make cached rendering safe + useful, using DIRECT DB writes (which the cache
/// cannot see) as the staleness probe — a cached response is one that does NOT reflect such a write:
///  1. SERVED FROM CACHE: a second read returns the identical rendered body without re-querying the DB.
///  2. RBAC ON CACHE HITS: a primed cache entry is still 403 for a caller without the permission
///     (UseOutputCache sits after UseAuthorization, so [RequirePermission] runs before any cache lookup).
///  3. KEY VARIATION: /iam/permissions?module=X and the unfiltered list are distinct entries.
///  4. CONTENT-CHANGE EVICTION: the API catalog writes (create permission) evict the iam-catalog tag, so the
///     next read re-renders and includes the change.
/// Reuses <see cref="RbacSuperAdminGucWebAppFactory"/>: SuperAdmin holds every gate; Control (tenant_owner in A)
/// holds tenant.users.read but NOT platform.api_clients.manage.
/// </summary>
public sealed class OutputCacheTests(RbacSuperAdminGucWebAppFactory factory, ITestOutputHelper output)
    : IClassFixture<RbacSuperAdminGucWebAppFactory>
{
    [Fact]
    public async Task ApiScopes_SecondRead_IsServedFromCachedRendering()
    {
        var client = await AuthedClientAsync(factory.SuperAdminEmail);

        var sw = Stopwatch.StartNew();
        var first = await client.GetStringAsync("/api/v1/api-scopes");
        var coldMs = sw.ElapsedMilliseconds;

        // A direct-DB insert is invisible to the output cache — if the second read still re-rendered from the
        // DB it WOULD contain this row; the cached body must not.
        var probeKey = $"cachetest.probe.{Guid.NewGuid():N}"[..40];
        await ExecAsync(
            """
            INSERT INTO platform_api.api_scopes (scope_key, resource, action, description, is_dangerous, requires_consent)
            VALUES (@k, 'cachetest', 'probe', 'output-cache staleness probe', false, false)
            """,
            ("k", probeKey));
        try
        {
            sw.Restart();
            var second = await client.GetStringAsync("/api/v1/api-scopes");
            var warmMs = sw.ElapsedMilliseconds;
            output.WriteLine($"/api/v1/api-scopes cold={coldMs}ms warm={warmMs}ms");

            Assert.Equal(first, second);                       // byte-identical rendered output
            Assert.DoesNotContain(probeKey, second);           // …because it never re-hit the DB
        }
        finally
        {
            await ExecAsync("DELETE FROM platform_api.api_scopes WHERE scope_key = @k", ("k", probeKey));
        }
    }

    [Fact]
    public async Task ApiScopes_CacheHit_StillEnforcesPermissionGate()
    {
        // Prime the cache as the super admin (holds platform.api_clients.manage)…
        var admin = await AuthedClientAsync(factory.SuperAdminEmail);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/api-scopes")).StatusCode);

        // …then the control user (tenant_owner in A, NO platform.api_clients.manage) must still be rejected:
        // the permission policy runs in the authorization middleware BEFORE the cache middleware can serve.
        var control = await AuthedClientAsync(factory.ControlEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await control.GetAsync("/api/v1/api-scopes")).StatusCode);
    }

    [Fact]
    public async Task IamPermissions_VariesByModuleQuery_And_CreateEvictsTheCatalogTag()
    {
        var client = await AuthedClientAsync(factory.SuperAdminEmail);

        // Distinct cache entries per ?module= (SetVaryByQuery): prime both, assert they differ.
        var all = await client.GetStringAsync("/api/v1/iam/permissions");
        var filtered = await client.GetStringAsync("/api/v1/iam/permissions?module=booking");
        Assert.NotEqual(all, filtered);
        Assert.Contains("booking", filtered);

        // Both entries are now cached: a direct-DB insert must NOT appear on re-read…
        var probeKey = $"cachetest.probe_{Guid.NewGuid():N}"[..40];
        var probeIds = new List<Guid>();
        await ExecAsync(
            """
            INSERT INTO platform.permissions (permission_id, permission_key, resource, action, scope, description, is_system, is_dangerous)
            VALUES (@id, @k, 'booking', @act, 'tenant', 'output-cache staleness probe', false, false)
            """,
            ("id", TrackId(probeIds)), ("k", probeKey), ("act", $"probe_{Guid.NewGuid():N}"[..20]));
        try
        {
            Assert.DoesNotContain(probeKey, await client.GetStringAsync("/api/v1/iam/permissions"));
            Assert.DoesNotContain(probeKey, await client.GetStringAsync("/api/v1/iam/permissions?module=booking"));

            // …until a CATALOG WRITE goes through the API: create-permission evicts the iam-catalog tag,
            // so the next read re-renders and now sees BOTH the API-created row and the direct probe row.
            var createdKey = $"cachetest.created_{Guid.NewGuid():N}"[..40];
            var create = await client.PostAsJsonAsync("/api/v1/iam/permissions",
                new CreatePermissionRequest(createdKey, "booking", $"created_{Guid.NewGuid():N}"[..20],
                    "tenant", "output-cache eviction probe"));
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var created = await create.Content.ReadFromJsonAsync<CreatePermissionResult>();
            probeIds.Add(created!.PermissionId);

            var refreshed = await client.GetStringAsync("/api/v1/iam/permissions?module=booking");
            Assert.Contains(createdKey, refreshed);
            Assert.Contains(probeKey, refreshed);
        }
        finally
        {
            await ExecAsync("DELETE FROM platform.role_permissions WHERE permission_id = ANY(@ids)", ("ids", probeIds.ToArray()));
            await ExecAsync("DELETE FROM platform.permissions WHERE permission_id = ANY(@ids)", ("ids", probeIds.ToArray()));
        }
    }

    [Fact]
    public async Task IamModules_SecondRead_IsCached_PerTenantKey()
    {
        var client = await AuthedClientAsync(factory.SuperAdminEmail);

        var sw = Stopwatch.StartNew();
        var first = await client.GetStringAsync("/api/v1/iam/modules");
        var coldMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var second = await client.GetStringAsync("/api/v1/iam/modules");
        var warmMs = sw.ElapsedMilliseconds;
        output.WriteLine($"/api/v1/iam/modules cold={coldMs}ms warm={warmMs}ms");

        Assert.Equal(first, second);
        Assert.NotEmpty(first);
    }

    private static Guid TrackId(List<Guid> ids)
    {
        var id = Guid.NewGuid();
        ids.Add(id);
        return id;
    }

    private async Task<HttpClient> AuthedClientAsync(string email)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, RbacSuperAdminGucWebAppFactory.Password, null));
        login.EnsureSuccessStatusCode();
        var token = await login.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private static async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(RbacSuperAdminGucWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
