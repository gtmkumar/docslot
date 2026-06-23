using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.SharedDataModel.Docslot.Auth;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Proves the RBAC admin WRITE paths now route through the schema's SECURITY DEFINER functions
/// (database/11_rbac_hardening.sql) under RLS as <c>docslot_app</c>, and that the database's
/// privilege-escalation guard (SQLSTATE 42501 → 403) and Separation-of-Duties trigger
/// (SQLSTATE 23000 → 409) surface as the correct HTTP status. Idempotent revoke is also asserted.
/// </summary>
public sealed class RbacHardeningWriteTests(RbacHardeningWebAppFactory factory)
    : IClassFixture<RbacHardeningWebAppFactory>
{
    [Fact]
    public async Task TenantOwner_Assigns_Role_To_User_Succeeds_Under_Rls()
    {
        var client = factory.CreateClient();
        await AuthenticateAsync(client, factory.OwnerEmail);

        // tenant_owner holds tenant.roles.assign and (PlainRole being empty) confers nothing it lacks →
        // the SECURITY DEFINER function inserts the row and returns its generated id.
        var resp = await client.PostAsJsonAsync("/api/v1/role-assignments",
            new AssignRoleRequest(factory.TargetUserId, factory.PlainRoleId, factory.TenantId, ExpiresAt: null));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<AssignRoleResult>();
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result!.UserTenantRoleId);

        // The row really exists (verify with the RLS-exempt owner connection).
        Assert.True(await AssignmentExistsAsync(result.UserTenantRoleId, mustBeLive: true));
    }

    [Fact]
    public async Task TenantViewer_Lacking_Assign_Permission_Gets_403()
    {
        var client = factory.CreateClient();
        await AuthenticateAsync(client, factory.ViewerEmail);

        // tenant_viewer holds neither tenant.roles.assign → the escalation guard fails closed with 403.
        var resp = await client.PostAsJsonAsync("/api/v1/role-assignments",
            new AssignRoleRequest(factory.TargetUserId, factory.PlainRoleId, factory.TenantId, ExpiresAt: null));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Grant_Override_For_Permission_Actor_Does_Not_Hold_Gets_403()
    {
        var client = factory.CreateClient();
        await AuthenticateAsync(client, factory.OwnerEmail);

        // tenant_owner holds platform.overrides.grant (passes the API gate AND the in-tenant override check),
        // but does NOT hold 'ai.models.manage'. The DB's no-escalation rule on set_user_permission_override
        // raises SQLSTATE 42501 when GRANTING a permission the actor lacks → 403.
        var resp = await client.PostAsJsonAsync("/api/v1/permission-overrides",
            new SetOverrideRequest(
                factory.TargetUserId, "ai.models.manage", IsAllowed: true,
                Reason: "test escalation guard", TenantId: factory.TenantId, ExpiresAt: null));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Sod_Incompatible_Pair_On_Same_User_Gets_409()
    {
        var client = factory.CreateClient();
        await AuthenticateAsync(client, factory.OwnerEmail);

        // First incompatible role assigns cleanly.
        var first = await client.PostAsJsonAsync("/api/v1/role-assignments",
            new AssignRoleRequest(factory.TargetUserId, factory.SodRoleAId, factory.TenantId, ExpiresAt: null));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // The second of a declared role_incompatibility pair trips the SoD trigger
        // (SQLSTATE 23000 / integrity_constraint_violation) → 409 Conflict.
        var second = await client.PostAsJsonAsync("/api/v1/role-assignments",
            new AssignRoleRequest(factory.TargetUserId, factory.SodRoleBId, factory.TenantId, ExpiresAt: null));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Revoke_Is_Idempotent_Second_Revoke_Reports_AlreadyRevoked()
    {
        var client = factory.CreateClient();
        await AuthenticateAsync(client, factory.OwnerEmail);

        // Assign, then revoke twice.
        var assign = await client.PostAsJsonAsync("/api/v1/role-assignments",
            new AssignRoleRequest(factory.TargetUserId, factory.PlainRoleId, factory.TenantId, ExpiresAt: null));
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
        var assigned = (await assign.Content.ReadFromJsonAsync<AssignRoleResult>())!;

        var firstRevoke = await client.PostAsJsonAsync("/api/v1/role-assignments/revoke",
            new RevokeRoleRequest(assigned.UserTenantRoleId, "test revoke"));
        Assert.Equal(HttpStatusCode.OK, firstRevoke.StatusCode);
        var revoked1 = (await firstRevoke.Content.ReadFromJsonAsync<RevokeRoleResult>())!;
        Assert.False(revoked1.AlreadyRevoked);

        var secondRevoke = await client.PostAsJsonAsync("/api/v1/role-assignments/revoke",
            new RevokeRoleRequest(assigned.UserTenantRoleId, "test revoke again"));
        Assert.Equal(HttpStatusCode.OK, secondRevoke.StatusCode);
        var revoked2 = (await secondRevoke.Content.ReadFromJsonAsync<RevokeRoleResult>())!;
        Assert.True(revoked2.AlreadyRevoked);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private async Task AuthenticateAsync(HttpClient client, string email)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, RbacHardeningWebAppFactory.Password, factory.TenantId));
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }

    private static async Task<bool> AssignmentExistsAsync(Guid userTenantRoleId, bool mustBeLive)
    {
        await using var conn = new NpgsqlConnection(RbacHardeningWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();
        var sql = mustBeLive
            ? "SELECT EXISTS(SELECT 1 FROM platform.user_tenant_roles WHERE user_tenant_role_id = @id AND revoked_at IS NULL)"
            : "SELECT EXISTS(SELECT 1 FROM platform.user_tenant_roles WHERE user_tenant_role_id = @id)";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", userTenantRoleId);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }
}
