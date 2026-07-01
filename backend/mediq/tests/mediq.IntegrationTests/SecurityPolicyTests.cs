using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using mediq.Application.Abstractions;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Security;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Tenant SECURITY-POLICY subsystem (issue #91). Proves REAL enforcement — every enforcement test asserts the
/// BLOCK, not merely that the toggle was stored: login is blocked outside hours (doctor exempt), from a
/// non-allow-listed IP, and for a required-2FA user without MFA; a sub-minimum password is rejected 422; the
/// masking toggle actually flips a receptionist's patient-detail read. Management endpoints are gated +
/// tenant-scoped. The app under test runs as <c>docslot_app</c> (RLS-enforced), the production path.
///
/// Each test establishes the exact policy it needs by writing <c>tenants.settings-&gt;'security'</c> directly
/// (owner connection) so run order is irrelevant AND a blocking policy never locks the actor out of the API
/// that would set it.
/// </summary>
public sealed class SecurityPolicyTests(SecurityPolicyWebAppFactory factory)
    : IClassFixture<SecurityPolicyWebAppFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- (1) Management: read + update, gated + tenant-scoped ------------------------------------

    [Fact]
    public async Task Owner_Reads_Defaults_Then_Updates_Policy_And_Reread_Reflects_It()
    {
        await factory.ClearPolicyAsync(factory.TenantA);
        var client = await AuthedClientAsync(factory.OwnerEmail, factory.TenantA);

        var get = await client.GetAsync("/api/v1/security/policy");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var defaults = await get.Content.ReadFromJsonAsync<SecurityPolicyDto>(Json);
        Assert.NotNull(defaults);
        Assert.Equal(MfaPolicyTiers.Optional, defaults!.MfaPolicy);   // absence → sensible defaults
        Assert.Equal(8, defaults.MinPasswordLength);
        Assert.True(defaults.MaskSensitiveForReceptionist);

        var put = await client.PutAsJsonAsync("/api/v1/security/policy",
            FullPolicy(mfaPolicy: MfaPolicyTiers.OwnersAdmins, minPasswordLength: 16, idleTimeoutMinutes: 20));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = await put.Content.ReadFromJsonAsync<SecurityPolicyDto>(Json);
        Assert.Equal(MfaPolicyTiers.OwnersAdmins, updated!.MfaPolicy);
        Assert.Equal(16, updated.MinPasswordLength);
        // Derived: owners_admins tier ⇒ at least the owner (mfa off) is pending enrolment.
        Assert.True(updated.StaffPendingMfaEnrolment >= 1);

        var reread = await (await client.GetAsync("/api/v1/security/policy")).Content.ReadFromJsonAsync<SecurityPolicyDto>(Json);
        Assert.Equal(16, reread!.MinPasswordLength);

        await factory.ClearPolicyAsync(factory.TenantA);   // leave a clean baseline for order-independent siblings
    }

    [Fact]
    public async Task Policy_Update_Rejects_Out_Of_Range_Values()
    {
        await factory.ClearPolicyAsync(factory.TenantA);
        var client = await AuthedClientAsync(factory.OwnerEmail, factory.TenantA);

        var badLen = await client.PutAsJsonAsync("/api/v1/security/policy", FullPolicy(minPasswordLength: 4)); // < 8
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badLen.StatusCode);

        var badHours = await client.PutAsJsonAsync("/api/v1/security/policy", FullPolicy(loginHoursStart: "9am"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badHours.StatusCode);

        var badTier = await client.PutAsJsonAsync("/api/v1/security/policy", FullPolicy(mfaPolicy: "sometimes"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badTier.StatusCode);
    }

    [Fact]
    public async Task Policy_Read_Is_Permission_Gated()
    {
        // Receptionist (tenant_staff) lacks tenant.settings.read → 403 at the gateway/authorization layer.
        var client = await AuthedClientAsync(factory.ReceptionistEmail, factory.TenantA);
        var get = await client.GetAsync("/api/v1/security/policy");
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
    }

    [Fact]
    public async Task Policy_Is_Tenant_Scoped_Not_Visible_Across_Tenants()
    {
        // Tenant A sets a distinctive min length; Tenant B's owner must see ITS OWN policy (defaults), never A's.
        await factory.WritePolicyAsync(factory.TenantA, FullPolicy(minPasswordLength: 25));
        await factory.ClearPolicyAsync(factory.TenantB);

        var bClient = await AuthedClientAsync(factory.OwnerBEmail, factory.TenantB);
        var bPolicy = await (await bClient.GetAsync("/api/v1/security/policy")).Content.ReadFromJsonAsync<SecurityPolicyDto>(Json);
        Assert.Equal(8, bPolicy!.MinPasswordLength);   // Tenant A's 25 never leaks into Tenant B

        await factory.ClearPolicyAsync(factory.TenantA);
    }

    // ---- (2) Login-hours enforcement -------------------------------------------------------------

    [Fact]
    public async Task Login_Blocked_Outside_Hours_But_Doctor_Is_Exempt()
    {
        // A 1-hour window starting 2h ahead (IST) can never contain "now" → everyone non-exempt is out of hours.
        var istNow = DateTime.UtcNow.AddMinutes(330);
        var start = istNow.AddHours(2).ToString("HH:mm");
        var end = istNow.AddHours(3).ToString("HH:mm");
        await factory.WritePolicyAsync(factory.TenantA,
            FullPolicy(restrictLoginHours: true, loginHoursStart: start, loginHoursEnd: end, doctorsExemptFromHours: true));

        var receptionist = await LoginAsync(factory.ReceptionistEmail, factory.TenantA);
        Assert.Equal(HttpStatusCode.Forbidden, receptionist.StatusCode);   // BLOCKED — outside hours

        var doctor = await LoginAsync(factory.DoctorEmail, factory.TenantA);
        Assert.Equal(HttpStatusCode.OK, doctor.StatusCode);                 // exempt — signs in fine

        await factory.ClearPolicyAsync(factory.TenantA);
    }

    [Fact]
    public async Task Login_Allowed_Within_Hours_Window()
    {
        await factory.WritePolicyAsync(factory.TenantA,
            FullPolicy(restrictLoginHours: true, loginHoursStart: "00:00", loginHoursEnd: "23:59"));
        var receptionist = await LoginAsync(factory.ReceptionistEmail, factory.TenantA);
        Assert.Equal(HttpStatusCode.OK, receptionist.StatusCode);
        await factory.ClearPolicyAsync(factory.TenantA);
    }

    // ---- (3) IP allow-list enforcement -----------------------------------------------------------

    [Fact]
    public async Task Login_Blocked_From_Non_Allowlisted_Ip_And_Allowed_From_Allowlisted()
    {
        await factory.WritePolicyAsync(factory.TenantA, FullPolicy(ipAllowlistEnabled: true));
        await factory.SeedAllowlistAsync(factory.TenantA, "203.0.113.0/24");

        var blocked = await LoginAsync(factory.ReceptionistEmail, factory.TenantA, sourceIp: "198.51.100.9");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);   // outside the allow-list → BLOCKED

        var allowed = await LoginAsync(factory.ReceptionistEmail, factory.TenantA, sourceIp: "203.0.113.50");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);          // inside the allow-list → OK

        await factory.ClearPolicyAsync(factory.TenantA);
    }

    // ---- (4) MFA-enrolment enforcement -----------------------------------------------------------

    [Fact]
    public async Task Login_Forces_Mfa_Enrolment_For_Covered_Tier_Only()
    {
        // 'all' tier: a user without mfa is forced to enrol (distinct outcome); a user WITH mfa signs in.
        await factory.WritePolicyAsync(factory.TenantA, FullPolicy(mfaPolicy: MfaPolicyTiers.All));

        var noMfa = await LoginAsync(factory.ReceptionistEmail, factory.TenantA);
        Assert.Equal(HttpStatusCode.Forbidden, noMfa.StatusCode);
        Assert.Contains(mediq.Utilities.Exceptions.MfaEnrollmentRequiredException.Code, await noMfa.Content.ReadAsStringAsync());

        var withMfa = await LoginAsync(factory.MfaEmail, factory.TenantA);
        Assert.Equal(HttpStatusCode.OK, withMfa.StatusCode);

        // 'owners_admins' tier: the owner (mfa off) is forced; the receptionist (not owner-tier) is NOT.
        await factory.WritePolicyAsync(factory.TenantA, FullPolicy(mfaPolicy: MfaPolicyTiers.OwnersAdmins));
        var owner = await LoginAsync(factory.OwnerEmail, factory.TenantA);
        Assert.Equal(HttpStatusCode.Forbidden, owner.StatusCode);
        Assert.Contains(mediq.Utilities.Exceptions.MfaEnrollmentRequiredException.Code, await owner.Content.ReadAsStringAsync());

        var recept = await LoginAsync(factory.ReceptionistEmail, factory.TenantA);
        Assert.Equal(HttpStatusCode.OK, recept.StatusCode);   // not covered by owners_admins → signs in

        await factory.ClearPolicyAsync(factory.TenantA);
    }

    // ---- (5) Password minimum-length enforcement -------------------------------------------------

    [Fact]
    public async Task ChangePassword_Below_Policy_Minimum_Is_Rejected()
    {
        await factory.WritePolicyAsync(factory.TenantA, FullPolicy(minPasswordLength: 20));
        var client = await AuthedClientAsync(factory.PwdEmail, factory.TenantA);

        var tooShort = await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new ChangePasswordRequest(SecurityPolicyWebAppFactory.Password, "Short1Pass9"));   // 11 chars < 20
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooShort.StatusCode);   // BLOCKED

        var okLen = await client.PostAsJsonAsync("/api/v1/auth/change-password",
            new ChangePasswordRequest(SecurityPolicyWebAppFactory.Password, "Aes-256-Gcm-Envelope-Passphrase!"));  // >= 20
        Assert.Equal(HttpStatusCode.OK, okLen.StatusCode);

        await factory.ClearPolicyAsync(factory.TenantA);
    }

    // ---- (6) Receptionist sensitive-field masking ------------------------------------------------

    [Fact]
    public async Task Masking_Toggle_Actually_Flips_Receptionist_Patient_Read()
    {
        // Masking ON: the receptionist (no medical_history.read) gets a masked phone.
        await factory.WritePolicyAsync(factory.TenantA, FullPolicy(maskSensitiveForReceptionist: true));
        var recept = await AuthedClientAsync(factory.ReceptionistEmail, factory.TenantA);
        recept.DefaultRequestHeaders.Add("X-Purpose-Of-Use", "treatment");

        var masked = await GetPatientDetailAsync(recept);
        Assert.NotEqual(factory.PatientPhone, masked.MaskedPhone);
        Assert.Contains('x', masked.MaskedPhone);

        // Masking OFF: the same receptionist now sees the full phone — the toggle REALLY changed the read.
        await factory.WritePolicyAsync(factory.TenantA, FullPolicy(maskSensitiveForReceptionist: false));
        var recept2 = await AuthedClientAsync(factory.ReceptionistEmail, factory.TenantA);
        recept2.DefaultRequestHeaders.Add("X-Purpose-Of-Use", "treatment");
        var full = await GetPatientDetailAsync(recept2);
        Assert.Equal(factory.PatientPhone, full.MaskedPhone);

        // Clinical staff (doctor holds medical_history.read) are exempt — full phone even with masking ON.
        await factory.WritePolicyAsync(factory.TenantA, FullPolicy(maskSensitiveForReceptionist: true));
        var doctor = await AuthedClientAsync(factory.DoctorEmail, factory.TenantA);
        doctor.DefaultRequestHeaders.Add("X-Purpose-Of-Use", "treatment");
        var doctorView = await GetPatientDetailAsync(doctor);
        Assert.Equal(factory.PatientPhone, doctorView.MaskedPhone);

        await factory.ClearPolicyAsync(factory.TenantA);
    }

    // ---- (7) IP allow-list management CRUD (gated) -----------------------------------------------

    [Fact]
    public async Task Owner_Manages_Ip_Allowlist_And_Receptionist_Is_Denied()
    {
        await factory.ClearPolicyAsync(factory.TenantA);   // keep the toggle off so the owner can sign in to manage

        var owner = await AuthedClientAsync(factory.OwnerEmail, factory.TenantA);
        var add = await owner.PostAsJsonAsync("/api/v1/security/ip-allowlist",
            new AddIpAllowlistRequest("192.0.2.0/24", "Office", null));
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var id = await add.Content.ReadFromJsonAsync<Guid>(Json);

        var list = await (await owner.GetAsync("/api/v1/security/ip-allowlist"))
            .Content.ReadFromJsonAsync<List<IpAllowlistEntryDto>>(Json);
        Assert.Contains(list!, e => e.AllowlistId == id && e.IsActive);

        var del = await owner.DeleteAsync($"/api/v1/security/ip-allowlist/{id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var afterList = await (await owner.GetAsync("/api/v1/security/ip-allowlist"))
            .Content.ReadFromJsonAsync<List<IpAllowlistEntryDto>>(Json);
        Assert.DoesNotContain(afterList!, e => e.AllowlistId == id && e.IsActive);

        // Receptionist lacks platform.ip_allowlist.manage → 403.
        var recept = await AuthedClientAsync(factory.ReceptionistEmail, factory.TenantA);
        var denied = await recept.GetAsync("/api/v1/security/ip-allowlist");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static object FullPolicy(
        string mfaPolicy = "optional", int minPasswordLength = 8, int idleTimeoutMinutes = 30,
        bool requireNewDeviceVerification = false, bool restrictLoginHours = false,
        string loginHoursStart = "00:00", string loginHoursEnd = "23:59",
        bool doctorsExemptFromHours = true, bool ipAllowlistEnabled = false,
        bool maskSensitiveForReceptionist = true)
        => new
        {
            mfaPolicy, minPasswordLength, idleTimeoutMinutes, requireNewDeviceVerification, restrictLoginHours,
            loginHoursStart, loginHoursEnd, doctorsExemptFromHours, ipAllowlistEnabled, maskSensitiveForReceptionist,
        };

    private async Task<HttpResponseMessage> LoginAsync(string email, Guid tenantId, string? sourceIp = null)
    {
        var client = factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(email, SecurityPolicyWebAppFactory.Password, tenantId)),
        };
        if (sourceIp is not null) req.Headers.Add("X-Test-Ip", sourceIp);
        return await client.SendAsync(req);
    }

    private async Task<HttpClient> AuthedClientAsync(string email, Guid tenantId)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, SecurityPolicyWebAppFactory.Password, tenantId));
        login.EnsureSuccessStatusCode();
        var token = await login.Content.ReadFromJsonAsync<TokenResponse>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private async Task<PatientDetailDto> GetPatientDetailAsync(HttpClient client)
    {
        var resp = await client.GetAsync($"/api/v1/patients/{factory.PatientId}");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PatientDetailDto>(Json))!;
    }
}
