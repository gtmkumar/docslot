using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Security;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// HTTP-contract proof of the begin/end impersonation endpoints (issue #3, app-side wiring). Drives the real
/// <c>mediq.Api</c> pipeline against the canonical DB via <see cref="PlatformWebAppFactory"/> (whose seeded
/// super_admin holds <c>platform.users.impersonate</c>, and whose <c>OtherTenantId</c> is a real tenant the
/// actor is NOT a member of — the impersonation target). Asserts the three auditor conditions for this slice:
///   1. begin mints a token carrying the server-signed <c>impersonated_tenant</c> claim ONLY after
///      <c>platform.begin_impersonation()</c> writes its audit row (actor = authenticated principal);
///   2. end re-mints a CLEAN token (no claim) and writes the symmetric <c>end_impersonation</c> audit row;
///   3. the endpoint is permission-gated — a logged-in user without the permission gets 403.
/// </summary>
public sealed class ImpersonationEndpointTests(PlatformWebAppFactory factory) : IClassFixture<PlatformWebAppFactory>
{
    private const string OwnerConn = PlatformWebAppFactory.ConnectionString;
    private const string ImpersonatedTenantClaim = "impersonated_tenant";

    [Fact]
    public async Task Begin_Mints_Claim_And_Audits_Then_End_Clears_Claim_And_Audits()
    {
        var client = factory.CreateClient();

        // Login as the seeded super_admin (holds platform.users.impersonate).
        var token = await LoginAsync(client, factory.SuperAdminEmail, PlatformWebAppFactory.SuperAdminPassword, factory.TenantId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // --- begin: open an audited session impersonating OtherTenantId ---
        var beginResp = await client.PostAsJsonAsync("/api/v1/auth/impersonation/begin",
            new BeginImpersonationRequest(factory.OtherTenantId, "support ticket #endpoint", token.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, beginResp.StatusCode);

        var begun = await beginResp.Content.ReadFromJsonAsync<ImpersonationResponse>();
        Assert.NotNull(begun);
        Assert.NotEqual(Guid.Empty, begun!.ImpersonationId);
        Assert.Equal(factory.OtherTenantId, begun.TargetTenantId);

        // The new access token carries the server-signed impersonated_tenant claim = the target.
        Assert.Equal(factory.OtherTenantId.ToString(), ClaimValue(begun.Token.AccessToken, ImpersonatedTenantClaim));

        // The open was audited by begin_impersonation() (not by the handler). >=1: the fixture's actor/target
        // are shared across tests in this class, so other begins may have written rows too — we assert presence.
        Assert.True(await AuditCountAsync("impersonate", factory.OtherTenantId, factory.SuperAdminUserId) >= 1);

        // --- end: close the session with the rotated token; the cleared claim must take effect ---
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", begun.Token.AccessToken);
        var endResp = await client.PostAsJsonAsync("/api/v1/auth/impersonation/end",
            new EndImpersonationRequest(begun.ImpersonationId, begun.Token.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, endResp.StatusCode);

        var ended = await endResp.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(ended);
        Assert.Null(ClaimValue(ended!.AccessToken, ImpersonatedTenantClaim));   // clean token — no impersonation
        Assert.True(await AuditCountAsync("end_impersonation", factory.OtherTenantId, factory.SuperAdminUserId) >= 1);
    }

    [Fact]
    public async Task Review_Surface_Lists_The_Open_Session_With_Masked_Actor()
    {
        var client = factory.CreateClient();
        var token = await LoginAsync(client, factory.SuperAdminEmail, PlatformWebAppFactory.SuperAdminPassword, factory.TenantId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var beginResp = await client.PostAsJsonAsync("/api/v1/auth/impersonation/begin",
            new BeginImpersonationRequest(factory.OtherTenantId, "review-surface ticket", token.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, beginResp.StatusCode);
        var begun = (await beginResp.Content.ReadFromJsonAsync<ImpersonationResponse>())!;

        // The Security & Compliance console lists it (super_admin holds platform.anomalies.review).
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", begun.Token.AccessToken);
        var listResp = await client.GetAsync("/api/v1/security/impersonation-sessions?take=200");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);

        var sessions = await listResp.Content.ReadFromJsonAsync<List<ImpersonationSessionDto>>();
        Assert.NotNull(sessions);
        var row = sessions!.SingleOrDefault(s => s.ImpersonationId == begun.ImpersonationId);
        Assert.NotNull(row);
        Assert.Equal("active", row!.Status);
        Assert.Equal(factory.OtherTenantId, row.TargetTenantId);
        Assert.Equal("review-surface ticket", row.Reason);
        // Actor is masked to initials — never the seeded full name.
        Assert.False(string.IsNullOrWhiteSpace(row.ActorLabel));
        Assert.DoesNotContain("Slice01", row.ActorLabel!);
    }

    [Fact]
    public async Task Begin_Without_The_Permission_Is_Forbidden()
    {
        // A plain user: tenant_owner in the test tenant (which does NOT carry the platform-scope
        // platform.users.impersonate permission).
        var userId = Guid.NewGuid();
        var email = $"impersonation.plain+{userId:N}@docslot.test";
        await using (var conn = new NpgsqlConnection(OwnerConn))
        {
            await conn.OpenAsync();
            await Exec(conn,
                """
                INSERT INTO platform.users (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, created_at, updated_at)
                VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Impersonation Plain User', true, true, false, NOW(), NOW())
                ON CONFLICT (email) DO NOTHING
                """,
                ("id", userId), ("email", email), ("pwd", PlatformWebAppFactory.SuperAdminPassword));
            await Exec(conn,
                """
                INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
                SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
                FROM platform.roles r WHERE r.role_key = 'tenant_owner' AND r.is_system = true
                ON CONFLICT DO NOTHING
                """,
                ("uid", userId), ("tid", factory.TenantId));
        }

        try
        {
            var client = factory.CreateClient();
            var token = await LoginAsync(client, email, PlatformWebAppFactory.SuperAdminPassword, factory.TenantId);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            var resp = await client.PostAsJsonAsync("/api/v1/auth/impersonation/begin",
                new BeginImpersonationRequest(factory.OtherTenantId, "should be blocked", token.RefreshToken));

            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally
        {
            await using var conn = new NpgsqlConnection(OwnerConn);
            await conn.OpenAsync();
            await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = @uid", ("uid", userId));
            await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id = @uid", ("uid", userId));
            await Exec(conn, "DELETE FROM platform.login_attempts WHERE email = @e", ("e", email));
            await Exec(conn, "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @uid",
                ("anon", $"deleted+{userId}@plain.test"), ("uid", userId));
        }
    }

    private static async Task<TokenResponse> LoginAsync(HttpClient client, string email, string password, Guid tenantId)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password, tenantId));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = await login.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token!;
    }

    private static string? ClaimValue(string jwt, string claimType) =>
        new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims.FirstOrDefault(c => c.Type == claimType)?.Value;

    private static async Task<int> AuditCountAsync(string action, Guid targetTenant, Guid actor)
    {
        await using var conn = new NpgsqlConnection(OwnerConn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT count(*) FROM platform.audit_log
            WHERE action = @action AND resource_type = 'tenant' AND resource_id = @target
              AND impersonator_user_id = @actor AND success = true
            """, conn);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("target", targetTenant);
        cmd.Parameters.AddWithValue("actor", actor);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object? Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
