using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.SharedDataModel.Docslot.Auth;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Proves the user-lifecycle endpoints behave as designed under RLS as <c>docslot_app</c> (the production
/// path through the SECURITY DEFINER functions):
/// <list type="bullet">
///   <item>Invite is wired live and escalation-SAFE — an actor with only <c>tenant.users.create</c> cannot
///   confer a role (the create rolls back atomically), while an admin can; an existing email LINKS without
///   overwriting the profile.</item>
///   <item>Deactivate/reactivate round-trips as a tenant-scoped membership revoke/restore; self-deactivate
///   is blocked; deactivating an admin is allowed while another admin remains.</item>
///   <item>Edit profile mutates only the whitelisted fields (never the email); an invalid language is 422.</item>
///   <item>Reset access sets the flags and clears the lockout; self-reset is blocked; a member lacking
///   <c>tenant.users.update</c> is 403.</item>
/// </list>
/// </summary>
public sealed class UserLifecycleTests(UserLifecycleWebAppFactory factory) : IClassFixture<UserLifecycleWebAppFactory>
{
    // ---- Invite (create) — wired live + escalation-by-proxy fix --------------------------------------

    [Fact]
    public async Task Admin_InviteUser_WithRole_CreatesAndAssigns()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var staffRole = await IamAdminWebAppFactory.SystemRoleIdAsync("tenant_staff");
        var email = $"{factory.InvitePrefix}.with-role@docslot.test";

        var resp = await client.PostAsJsonAsync($"/api/v1/tenants/{factory.TenantId}/users",
            new CreateUserRequest(email, "New Hire", null, "en", staffRole));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<CreateUserResult>())!;
        Assert.False(result.AlreadyExisted);
        Assert.NotEqual(Guid.Empty, result.UserId);

        var users = await ListUsersAsync(client);
        var u = users.Single(x => x.UserId == result.UserId);
        Assert.Contains(u.Roles, r => r.RoleKey == "tenant_staff");
    }

    [Fact]
    public async Task Admin_InviteExistingEmail_LinksWithoutOverwriting()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var staffRole = await IamAdminWebAppFactory.SystemRoleIdAsync("tenant_staff");

        var resp = await client.PostAsJsonAsync($"/api/v1/tenants/{factory.TenantId}/users",
            new CreateUserRequest(factory.MemberEmail, "Should Not Overwrite", null, "en", staffRole));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<CreateUserResult>())!;
        Assert.True(result.AlreadyExisted);
        Assert.Equal(factory.MemberUserId, result.UserId);

        var fullName = (string)(await UserLifecycleWebAppFactory.UserScalarAsync(factory.MemberUserId, "full_name"))!;
        Assert.NotEqual("Should Not Overwrite", fullName);   // existing profile preserved
    }

    [Fact]
    public async Task LimitedInviter_InviteWithRole_Forbidden_AndRollsBack()
    {
        var client = await AuthedClientAsync(factory.InviterEmail);
        var staffRole = await IamAdminWebAppFactory.SystemRoleIdAsync("tenant_staff");
        var email = $"{factory.InvitePrefix}.escalation@docslot.test";

        // Inviter holds tenant.users.create but NOT tenant.roles.assign → conferring ANY role is the
        // escalation-by-proxy the fix closes. The create + assign share one UoW transaction.
        var resp = await client.PostAsJsonAsync($"/api/v1/tenants/{factory.TenantId}/users",
            new CreateUserRequest(email, "Escalation Attempt", null, "en", staffRole));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.False(await UserLifecycleWebAppFactory.LiveUserExistsAsync(email));   // atomic rollback, no orphan
    }

    [Fact]
    public async Task LimitedInviter_InviteWithoutRole_Succeeds()
    {
        var client = await AuthedClientAsync(factory.InviterEmail);
        var email = $"{factory.InvitePrefix}.norole@docslot.test";

        var resp = await client.PostAsJsonAsync($"/api/v1/tenants/{factory.TenantId}/users",
            new CreateUserRequest(email, "No Role Hire", null, "en", null));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.True(await UserLifecycleWebAppFactory.LiveUserExistsAsync(email));
    }

    // ---- Deactivate / reactivate --------------------------------------------------------------------

    [Fact]
    public async Task Admin_DeactivateThenReactivate_Member_RoundTrips()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);

        var deact = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.MemberUserId}/status",
            new SetUserStatusRequest(false, "left the clinic"));
        Assert.Equal(HttpStatusCode.OK, deact.StatusCode);

        var afterDeact = (await ListUsersAsync(client)).Single(x => x.UserId == factory.MemberUserId);
        Assert.False(afterDeact.IsActive);
        Assert.Empty(afterDeact.Roles);   // memberships revoked → no chips, still listed (reactivatable)

        var react = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.MemberUserId}/status",
            new SetUserStatusRequest(true, "rejoined"));
        Assert.Equal(HttpStatusCode.OK, react.StatusCode);

        var afterReact = (await ListUsersAsync(client)).Single(x => x.UserId == factory.MemberUserId);
        Assert.True(afterReact.IsActive);
        Assert.Contains(afterReact.Roles, r => r.RoleKey == "tenant_staff");
    }

    [Fact]
    public async Task Admin_DeactivateSelf_Forbidden()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var resp = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.AdminUserId}/status",
            new SetUserStatusRequest(false, "self"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_DeactivateOtherAdmin_AllowedWhileAnotherRemains()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);

        var deact = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.Admin2UserId}/status",
            new SetUserStatusRequest(false, "rotate admin2"));
        Assert.Equal(HttpStatusCode.OK, deact.StatusCode);

        // restore so the tenant keeps two admins for any other test
        var react = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.Admin2UserId}/status",
            new SetUserStatusRequest(true, "restore admin2"));
        Assert.Equal(HttpStatusCode.OK, react.StatusCode);
    }

    [Fact]
    public async Task Member_LacksUpdate_Deactivate_Forbidden()
    {
        var client = await AuthedClientAsync(factory.MemberEmail);   // tenant_staff: no tenant.users.update
        var resp = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.Admin2UserId}/status",
            new SetUserStatusRequest(false, "nope"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- Edit profile -------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_EditMemberProfile_UpdatesWhitelistedFields_NotEmail()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var emailBefore = (string)(await UserLifecycleWebAppFactory.UserScalarAsync(factory.MemberUserId, "email"))!;

        var resp = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.MemberUserId}",
            new UpdateUserProfileRequest("Renamed Member", "+919812345678", "hi"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Equal("Renamed Member", (string)(await UserLifecycleWebAppFactory.UserScalarAsync(factory.MemberUserId, "full_name"))!);
        Assert.Equal("hi", (string)(await UserLifecycleWebAppFactory.UserScalarAsync(factory.MemberUserId, "preferred_language"))!);
        Assert.Equal(emailBefore, (string)(await UserLifecycleWebAppFactory.UserScalarAsync(factory.MemberUserId, "email"))!);
    }

    [Fact]
    public async Task EditProfile_InvalidLanguage_422()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var resp = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.MemberUserId}",
            new UpdateUserProfileRequest("Whatever", null, "fr"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    // ---- Reset access -------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_ResetMemberAccess_SetsFlagsAndClearsLock()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        await LockUserAsync(factory.MemberUserId);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.MemberUserId}/reset-access",
            new ResetAccessRequest("forgot password"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.True((bool)(await UserLifecycleWebAppFactory.UserScalarAsync(factory.MemberUserId, "must_change_password"))!);
        var locked = await UserLifecycleWebAppFactory.UserScalarAsync(factory.MemberUserId, "locked_until");
        Assert.True(locked is null or DBNull);
    }

    [Fact]
    public async Task Admin_ResetSelf_Forbidden()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.AdminUserId}/reset-access",
            new ResetAccessRequest("self"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private async Task<HttpClient> AuthedClientAsync(string email)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, UserLifecycleWebAppFactory.Password, factory.TenantId));
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private async Task<List<UserListItemDto>> ListUsersAsync(HttpClient client)
    {
        var resp = await client.GetAsync($"/api/v1/tenants/{factory.TenantId}/users");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<List<UserListItemDto>>())!;
    }

    private static async Task LockUserAsync(Guid userId)
    {
        await using var conn = new NpgsqlConnection(UserLifecycleWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE platform.users SET locked_until = NOW() + interval '1 hour', failed_login_count = 4 WHERE user_id = @u", conn);
        cmd.Parameters.AddWithValue("u", userId);
        await cmd.ExecuteNonQueryAsync();
    }
}
