using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using mediq.SharedDataModel.Docslot.Admin;
using mediq.SharedDataModel.Docslot.Auth;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Proves the invitations subsystem (issue #89) behaves as designed under RLS as <c>docslot_app</c> (the
/// production path through the SECURITY DEFINER functions):
/// <list type="bullet">
///   <item>Create mints a pending invite and returns the ONE-TIME plaintext token; the DB stores only its
///   SHA-256 hash (never the plaintext).</item>
///   <item>List is tenant-isolated (a second tenant's invite never appears) and gated on tenant.users.read.</item>
///   <item>Resend rotates the token, bumps resend_count, and extends expiry (the old token stops working).</item>
///   <item>Revoke flips the invite to revoked; the token then fails accept.</item>
///   <item>Accept with a VALID token provisions the user, assigns the pre-vetted role, and marks accepted;
///   expired / revoked / already-used / garbage tokens are all refused with one generic 422.</item>
///   <item>The R3 no-escalation guard: an actor with only tenant.users.create cannot invite WITH a role (403,
///   nothing persisted) but CAN invite without one.</item>
/// </list>
/// </summary>
public sealed class InvitationTests(InvitationWebAppFactory factory) : IClassFixture<InvitationWebAppFactory>
{
    // ---- Create --------------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_Create_ReturnsPendingAndPlaintextTokenNotInDb()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var email = $"{factory.InvitePrefix}.create@docslot.test";

        var resp = await client.PostAsJsonAsync($"/api/v1/tenants/{factory.TenantId}/invitations",
            new CreateInvitationRequest(email));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<InvitationTokenResult>())!;
        Assert.NotEqual(Guid.Empty, result.InvitationId);
        Assert.False(string.IsNullOrWhiteSpace(result.Token));
        Assert.Equal(0, result.ResendCount);
        Assert.True(result.ExpiresAt > DateTime.UtcNow.AddDays(6));

        // The DB persists ONLY the SHA-256 hash of the token — never the plaintext.
        var storedHash = (string)(await InvitationWebAppFactory.InvitationScalarAsync(result.InvitationId, "token_hash"))!;
        var status = (string)(await InvitationWebAppFactory.InvitationScalarAsync(result.InvitationId, "status"))!;
        Assert.Equal("pending", status);
        Assert.NotEqual(result.Token, storedHash);
        Assert.Equal(Sha256Hex(result.Token), storedHash);
    }

    // ---- List (tenant isolation + gating) ------------------------------------------------------------

    [Fact]
    public async Task List_IsTenantIsolated()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var mine = $"{factory.InvitePrefix}.list-mine@docslot.test";
        await client.PostAsJsonAsync($"/api/v1/tenants/{factory.TenantId}/invitations", new CreateInvitationRequest(mine));

        var resp = await client.GetAsync($"/api/v1/tenants/{factory.TenantId}/invitations?status=pending");
        resp.EnsureSuccessStatusCode();
        var list = (await resp.Content.ReadFromJsonAsync<InvitationListDto>())!;

        Assert.Contains(list.Items, i => i.InvitedEmail == mine);
        Assert.DoesNotContain(list.Items, i => i.InvitationId == factory.OtherTenantInvitationId);   // other tenant's invite hidden
        Assert.All(list.Items, i => Assert.Equal("pending", i.Status));
        Assert.Equal(list.Items.Count, list.Count);
    }

    [Fact]
    public async Task List_WithoutReadPermission_Forbidden()
    {
        // The limited inviter holds tenant.users.create but NOT tenant.users.read.
        var client = await AuthedClientAsync(factory.InviterEmail);
        var resp = await client.GetAsync($"/api/v1/tenants/{factory.TenantId}/invitations");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ---- Resend --------------------------------------------------------------------------------------

    [Fact]
    public async Task Resend_BumpsCountAndExpiry_AndRotatesToken()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var email = $"{factory.InvitePrefix}.resend@docslot.test";
        var created = (await (await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/invitations", new CreateInvitationRequest(email)))
            .Content.ReadFromJsonAsync<InvitationTokenResult>())!;

        var resp = await client.PostAsync(
            $"/api/v1/tenants/{factory.TenantId}/invitations/{created.InvitationId}/resend", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var resent = (await resp.Content.ReadFromJsonAsync<InvitationTokenResult>())!;

        Assert.Equal(created.InvitationId, resent.InvitationId);
        Assert.Equal(1, resent.ResendCount);
        Assert.NotEqual(created.Token, resent.Token);
        Assert.True(resent.ExpiresAt >= created.ExpiresAt);

        // The OLD token is invalidated (hash rotated) — accepting with it fails.
        var oldAccept = await factory.CreateClient().PostAsJsonAsync("/api/v1/invitations/accept",
            new AcceptInvitationRequest(created.Token, "Old Token User", "Sup3rSecret!"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, oldAccept.StatusCode);
    }

    // ---- Revoke --------------------------------------------------------------------------------------

    [Fact]
    public async Task Revoke_MarksRevoked_AndTokenNoLongerAccepts()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var email = $"{factory.InvitePrefix}.revoke@docslot.test";
        var created = (await (await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/invitations", new CreateInvitationRequest(email)))
            .Content.ReadFromJsonAsync<InvitationTokenResult>())!;

        var resp = await client.PostAsync(
            $"/api/v1/tenants/{factory.TenantId}/invitations/{created.InvitationId}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<RevokeInvitationResult>())!;
        Assert.False(result.AlreadyInactive);

        var status = (string)(await InvitationWebAppFactory.InvitationScalarAsync(created.InvitationId, "status"))!;
        Assert.Equal("revoked", status);

        var accept = await factory.CreateClient().PostAsJsonAsync("/api/v1/invitations/accept",
            new AcceptInvitationRequest(created.Token, "Revoked User", "Sup3rSecret!"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, accept.StatusCode);
    }

    // ---- Accept (happy path) -------------------------------------------------------------------------

    [Fact]
    public async Task Accept_ValidToken_ProvisionsUserAssignsRoleAndMarksAccepted()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var staffRole = await IamAdminWebAppFactory.SystemRoleIdAsync("tenant_staff");
        var email = $"{factory.InvitePrefix}.accept@docslot.test";
        var created = (await (await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/invitations", new CreateInvitationRequest(email, staffRole)))
            .Content.ReadFromJsonAsync<InvitationTokenResult>())!;

        const string chosenPassword = "MyN3wPassw0rd!";
        var accept = await factory.CreateClient().PostAsJsonAsync("/api/v1/invitations/accept",
            new AcceptInvitationRequest(created.Token, "Accepted Hire", chosenPassword));

        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var result = (await accept.Content.ReadFromJsonAsync<AcceptInvitationResult>())!;
        Assert.False(result.AlreadyExisted);
        Assert.Equal(factory.TenantId, result.TenantId);
        Assert.NotEqual(Guid.Empty, result.UserId);

        // User provisioned + role assigned + invite marked accepted with accepted_user_id.
        Assert.True(await InvitationWebAppFactory.LiveUserExistsAsync(email));
        Assert.Equal(1, await InvitationWebAppFactory.ActiveRoleCountAsync(email, factory.TenantId));
        Assert.Equal("accepted", (string)(await InvitationWebAppFactory.InvitationScalarAsync(created.InvitationId, "status"))!);
        Assert.Equal(result.UserId, (Guid)(await InvitationWebAppFactory.InvitationScalarAsync(created.InvitationId, "accepted_user_id"))!);

        // The invitee's chosen password actually works (proves provisioning set the credential).
        var login = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, chosenPassword, factory.TenantId));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Single-use: a second accept with the same token is refused.
        var replay = await factory.CreateClient().PostAsJsonAsync("/api/v1/invitations/accept",
            new AcceptInvitationRequest(created.Token, "Replay", chosenPassword));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, replay.StatusCode);
    }

    // ---- Accept (refusals) ---------------------------------------------------------------------------

    [Fact]
    public async Task Accept_ExpiredToken_Refused()
    {
        var client = await AuthedClientAsync(factory.AdminEmail);
        var email = $"{factory.InvitePrefix}.expired@docslot.test";
        var created = (await (await client.PostAsJsonAsync(
            $"/api/v1/tenants/{factory.TenantId}/invitations", new CreateInvitationRequest(email)))
            .Content.ReadFromJsonAsync<InvitationTokenResult>())!;

        // Force the window into the past (owner conn, RLS-exempt) — simulates an aged invite.
        await ForceExpiredAsync(created.InvitationId);

        var accept = await factory.CreateClient().PostAsJsonAsync("/api/v1/invitations/accept",
            new AcceptInvitationRequest(created.Token, "Too Late", "Sup3rSecret!"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, accept.StatusCode);
        Assert.False(await InvitationWebAppFactory.LiveUserExistsAsync(email));   // no provisioning
    }

    [Fact]
    public async Task Accept_GarbageToken_Refused()
    {
        var accept = await factory.CreateClient().PostAsJsonAsync("/api/v1/invitations/accept",
            new AcceptInvitationRequest("not-a-real-token-" + Guid.NewGuid().ToString("N"), "Nobody", "Sup3rSecret!"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, accept.StatusCode);
    }

    // ---- R3 no-escalation guard ----------------------------------------------------------------------

    [Fact]
    public async Task LimitedInviter_InviteWithRole_Forbidden_AndNothingPersisted()
    {
        var client = await AuthedClientAsync(factory.InviterEmail);
        var staffRole = await IamAdminWebAppFactory.SystemRoleIdAsync("tenant_staff");
        var email = $"{factory.InvitePrefix}.escalation@docslot.test";

        // Inviter holds tenant.users.create but NOT tenant.roles.assign → attaching ANY role is escalation-by-proxy.
        var resp = await client.PostAsJsonAsync($"/api/v1/tenants/{factory.TenantId}/invitations",
            new CreateInvitationRequest(email, staffRole));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.False(await PendingInviteExistsAsync(email));   // atomic — no orphan invitation row
    }

    [Fact]
    public async Task LimitedInviter_InviteWithoutRole_Succeeds()
    {
        var client = await AuthedClientAsync(factory.InviterEmail);
        var email = $"{factory.InvitePrefix}.norole@docslot.test";

        var resp = await client.PostAsJsonAsync($"/api/v1/tenants/{factory.TenantId}/invitations",
            new CreateInvitationRequest(email));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.True(await PendingInviteExistsAsync(email));
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private async Task<HttpClient> AuthedClientAsync(string email)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, InvitationWebAppFactory.Password, factory.TenantId));
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private static string Sha256Hex(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static async Task ForceExpiredAsync(Guid invitationId)
    {
        await using var conn = new Npgsql.NpgsqlConnection(InvitationWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "UPDATE platform.invitations SET expires_at = NOW() - interval '1 hour' WHERE invitation_id = @i", conn);
        cmd.Parameters.AddWithValue("i", invitationId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> PendingInviteExistsAsync(string email)
    {
        await using var conn = new Npgsql.NpgsqlConnection(InvitationWebAppFactory.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM platform.invitations WHERE invited_email = @e AND status = 'pending')", conn);
        cmd.Parameters.AddWithValue("e", email);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }
}
