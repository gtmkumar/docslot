using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Web;
using mediq.SharedDataModel.Docslot.Auth;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Proves the password-reset subsystem (self-service + admin-initiated) behaves as designed under RLS as
/// <c>docslot_app</c> (the production path through the SECURITY DEFINER functions):
/// <list type="bullet">
///   <item>forgot-password never enumerates: an unknown email mints NO token yet still returns 200; a known
///   active user mints exactly one live token.</item>
///   <item>reset-password (happy path) sets the new password, marks the token used, revokes active sessions,
///   and lets the user log in with the new password (old fails). Expired / used / reused tokens → generic 422.</item>
///   <item>admin reset (tenant + platform routes) returns the one-time link, which is consumable.</item>
///   <item>the R3 no-escalation guard: a limited admin cannot reset a higher-privileged user → 403.</item>
///   <item>a below-floor new password is rejected → 422.</item>
/// </list>
/// </summary>
public sealed class PasswordResetTests(PasswordResetWebAppFactory factory) : IClassFixture<PasswordResetWebAppFactory>
{
    // ---- forgot-password (anti-enumeration) ----------------------------------------------------------

    [Fact]
    public async Task Forgot_UnknownEmail_Returns200_AndMintsNoToken()
    {
        var before = await PasswordResetWebAppFactory.TotalTokenRowsAsync();

        var resp = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password",
            new ForgotPasswordRequest($"nobody+{Guid.NewGuid():N}@docslot.test"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<ForgotPasswordResult>())!;
        Assert.True(result.Requested);

        // No user → no token minted (no enumeration signal).
        Assert.Equal(before, await PasswordResetWebAppFactory.TotalTokenRowsAsync());
    }

    [Fact]
    public async Task Forgot_KnownActiveUser_Returns200_AndMintsExactlyOneLiveToken()
    {
        factory.Notifier.Reset();
        await PasswordResetWebAppFactory.ClearTokensAsync(factory.StaffSelfUserId);

        var resp = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password",
            new ForgotPasswordRequest(factory.StaffSelfEmail));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True((await resp.Content.ReadFromJsonAsync<ForgotPasswordResult>())!.Requested);

        Assert.Equal(1, await PasswordResetWebAppFactory.TokenRowCountAsync(factory.StaffSelfUserId, liveOnly: true));
        // The one-time token went to the (offline) notifier, never the response.
        Assert.Equal(1, factory.Notifier.CountFor(factory.StaffSelfEmail));
    }

    // ---- reset-password (happy path) -----------------------------------------------------------------

    [Fact]
    public async Task Reset_HappyPath_ChangesPassword_MarksUsed_RevokesSessions()
    {
        factory.Notifier.Reset();
        await PasswordResetWebAppFactory.ClearTokensAsync(factory.StaffSelfUserId);

        // Establish an active session for the user (which the reset must revoke).
        var login = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.StaffSelfEmail, PasswordResetWebAppFactory.Password, factory.TenantId));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(await PasswordResetWebAppFactory.ActiveSessionCountAsync(factory.StaffSelfUserId) >= 1);

        // Self-service mint → recover the one-time token from the notifier.
        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password",
            new ForgotPasswordRequest(factory.StaffSelfEmail));
        var token = factory.Notifier.LatestTokenFor(factory.StaffSelfEmail);
        Assert.False(string.IsNullOrWhiteSpace(token));

        const string newPassword = "N3wStr0ngPass!";
        var reset = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(token!, newPassword));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.True((await reset.Content.ReadFromJsonAsync<ResetPasswordResult>())!.Reset);

        // Token consumed; all sessions revoked with the reset reason.
        Assert.Equal(0, await PasswordResetWebAppFactory.TokenRowCountAsync(factory.StaffSelfUserId, liveOnly: true));
        Assert.Equal(0, await PasswordResetWebAppFactory.ActiveSessionCountAsync(factory.StaffSelfUserId));
        Assert.True(await PasswordResetWebAppFactory.HasPasswordResetRevokedSessionAsync(factory.StaffSelfUserId));

        // The new password works; the old one does not.
        var loginNew = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.StaffSelfEmail, newPassword, factory.TenantId));
        Assert.Equal(HttpStatusCode.OK, loginNew.StatusCode);

        var loginOld = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.StaffSelfEmail, PasswordResetWebAppFactory.Password, factory.TenantId));
        Assert.Equal(HttpStatusCode.Unauthorized, loginOld.StatusCode);
    }

    // ---- reset-password (refusals) -------------------------------------------------------------------

    [Fact]
    public async Task Reset_ExpiredToken_Refused_Generic422()
    {
        factory.Notifier.Reset();
        await PasswordResetWebAppFactory.ClearTokensAsync(factory.StaffTenantUserId);

        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password",
            new ForgotPasswordRequest(factory.StaffTenantEmail));
        var token = factory.Notifier.LatestTokenFor(factory.StaffTenantEmail)!;

        await PasswordResetWebAppFactory.ExpireTokensAsync(factory.StaffTenantUserId);

        var reset = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(token, "N3wStr0ngPass!"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reset.StatusCode);
    }

    [Fact]
    public async Task Reset_UsedTokenReuse_Blocked_Generic422()
    {
        factory.Notifier.Reset();
        await PasswordResetWebAppFactory.ClearTokensAsync(factory.StaffPlatformUserId);

        await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password",
            new ForgotPasswordRequest(factory.StaffPlatformEmail));
        var token = factory.Notifier.LatestTokenFor(factory.StaffPlatformEmail)!;

        // First redeem succeeds.
        var first = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(token, "N3wStr0ngPass!"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Reusing the same token is refused with the same generic 422.
        var replay = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(token, "An0therStr0ng!"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, replay.StatusCode);
    }

    [Fact]
    public async Task Reset_GarbageToken_Refused_Generic422()
    {
        var reset = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest("not-a-real-token-" + Guid.NewGuid().ToString("N"), "N3wStr0ngPass!"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reset.StatusCode);
    }

    [Fact]
    public async Task Reset_BelowPasswordFloor_Rejected_422()
    {
        var reset = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest("some-token", "short"));   // < 8 chars → validation 422 (before token lookup)
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reset.StatusCode);
    }

    // ---- admin-initiated reset -----------------------------------------------------------------------

    [Fact]
    public async Task Admin_TenantRoute_ReturnsLink_AndTokenIsConsumable()
    {
        factory.Notifier.Reset();
        await PasswordResetWebAppFactory.ClearTokensAsync(factory.StaffTenantUserId);

        var admin = await AuthedClientAsync(factory.AdminEmail);
        var resp = await admin.PostAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.StaffTenantUserId}/reset-password", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<AdminResetPasswordResult>())!;
        Assert.False(string.IsNullOrWhiteSpace(result.ResetLink));
        Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow);

        // The link carries the one-time token; the USER completes the reset with it.
        var token = TokenFromLink(result.ResetLink);
        const string newPassword = "AdminSetP4ss!";
        var reset = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(token, newPassword));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var loginNew = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.StaffTenantEmail, newPassword, factory.TenantId));
        Assert.Equal(HttpStatusCode.OK, loginNew.StatusCode);
    }

    [Fact]
    public async Task Admin_PlatformRoute_BySuperAdmin_ReturnsLink_AndTokenIsConsumable()
    {
        factory.Notifier.Reset();
        await PasswordResetWebAppFactory.ClearTokensAsync(factory.StaffPlatformUserId);

        var super = await AuthedClientAsync(factory.SuperAdminEmail);
        var resp = await super.PostAsync($"/api/v1/users/{factory.StaffPlatformUserId}/reset-password", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<AdminResetPasswordResult>())!;
        Assert.False(string.IsNullOrWhiteSpace(result.ResetLink));

        var token = TokenFromLink(result.ResetLink);
        const string newPassword = "SuperSetP4ss!";
        var reset = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest(token, newPassword));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var loginNew = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.StaffPlatformEmail, newPassword, factory.TenantId));
        Assert.Equal(HttpStatusCode.OK, loginNew.StatusCode);
    }

    // ---- R3 no-escalation guard ----------------------------------------------------------------------

    [Fact]
    public async Task Admin_LimitedAdmin_ResettingHigherPrivilegedUser_Forbidden()
    {
        // The limited admin holds ONLY tenant.users.update; the target (tenant_owner) confers permissions the
        // actor does not hold → the R3 guard in admin_request_password_reset rejects it (403), nothing minted.
        await PasswordResetWebAppFactory.ClearTokensAsync(factory.AdminUserId);

        var limited = await AuthedClientAsync(factory.LimitedAdminEmail);
        var resp = await limited.PostAsync(
            $"/api/v1/tenants/{factory.TenantId}/users/{factory.AdminUserId}/reset-password", null);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(0, await PasswordResetWebAppFactory.TokenRowCountAsync(factory.AdminUserId, liveOnly: false));

        // The DENIED attempt is audited (Success=false) — a probing admin leaves a trail (auditor finding #1).
        Assert.True(await PasswordResetWebAppFactory.HasDeniedResetAuditAsync(factory.AdminUserId, factory.LimitedAdminUserId));
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private async Task<HttpClient> AuthedClientAsync(string email)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, PasswordResetWebAppFactory.Password, factory.TenantId));
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private static string TokenFromLink(string resetLink)
    {
        var q = resetLink.Contains('?') ? resetLink[(resetLink.IndexOf('?') + 1)..] : resetLink;
        var token = HttpUtility.ParseQueryString(q).Get("token");
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token!;
    }
}
