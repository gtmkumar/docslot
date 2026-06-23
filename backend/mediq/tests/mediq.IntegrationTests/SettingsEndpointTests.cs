using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using mediq.Application.Features.Docslot.Settings;
using mediq.SharedDataModel.Docslot.Auth;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Tenant Settings read + PATCH against the live canonical DB (docslot.healthcare_facilities). A tenant_owner
/// (holds tenant.settings.read / .update) reads the settings, PATCHes appointmentSettings.slotDurationMinutes
/// plus a business-hours day, and re-reads to confirm persistence. The PATCH is tenant-scoped (the row carries
/// the JWT tenant_id) and the whatsapp_access_token secret is NEVER returned. Uses the slice-03 fixture
/// (seeds a verified WhatsApp + HFR facility for this tenant).
///
/// The tests share one fixture instance and run sequentially in a non-deterministic order, so each test that
/// asserts a known baseline first PATCHes that baseline through the API (idempotent, order-independent) — the
/// settings row is mutable shared state.
/// </summary>
public sealed class SettingsEndpointTests(DocslotWebAppFactory factory) : IClassFixture<DocslotWebAppFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The seeded baseline (matches DocslotWebAppFactory + database/seed_demo_facility.sql).
    private static readonly UpdateSettingsRequest Baseline = new(
        BusinessHours: new Dictionary<string, DayHours>
        {
            ["mon"] = new("09:00", "18:00", false),
            ["tue"] = new("09:00", "18:00", false),
            ["wed"] = new("09:00", "18:00", false),
            ["thu"] = new("09:00", "18:00", false),
            ["fri"] = new("09:00", "18:00", false),
            ["sat"] = new("09:00", "14:00", false),
            ["sun"] = new(null, null, true),
        },
        AppointmentSettings: new AppointmentSettingsDto(
            SlotDurationMinutes: 15, BookingCutoffHours: 2, AutoConfirm: true,
            MaxAdvanceDays: 30, AllowOverbooking: false, ReminderHoursBefore: 24, NoShowGraceMinutes: 15));

    [Fact]
    public async Task Get_Returns_Seeded_Settings_Without_Whatsapp_Token()
    {
        var client = await AuthedClientAsync();
        // Reset to the known baseline first (this test must not depend on whether the PATCH test ran before it).
        (await client.PatchAsJsonAsync("/api/v1/settings", Baseline)).EnsureSuccessStatusCode();

        var resp = await client.GetAsync("/api/v1/settings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The whatsapp_access_token must never appear anywhere in the serialized response.
        var raw = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-secret-token-never-leak", raw);
        Assert.DoesNotContain("accessToken", raw, StringComparison.OrdinalIgnoreCase);

        var settings = JsonSerializer.Deserialize<SettingsDto>(raw, Json);
        Assert.NotNull(settings);
        Assert.Equal("hospital", settings!.FacilityType);
        Assert.Equal(15, settings.AppointmentSettings.SlotDurationMinutes);
        Assert.True(settings.AppointmentSettings.AutoConfirm);
        Assert.True(settings.BusinessHours.ContainsKey("mon"));
        Assert.Equal("09:00", settings.BusinessHours["mon"].Open);
        Assert.True(settings.BusinessHours["sun"].Closed);

        // WhatsApp connection status surfaces (connected/verified) but never the token.
        Assert.True(settings.WhatsApp.Connected);
        Assert.Equal("PNID_SLICE_SETTINGS", settings.WhatsApp.PhoneNumberId);
        Assert.NotNull(settings.WhatsApp.VerifiedAt);
        Assert.Equal("verified", settings.Hfr.Status);
    }

    [Fact]
    public async Task Patch_Updates_AppointmentSettings_And_BusinessHours_And_Get_Reflects_It()
    {
        var client = await AuthedClientAsync();
        // Start from a known baseline so the untouched-section assertion ('mon' = 09:00) is deterministic.
        (await client.PatchAsJsonAsync("/api/v1/settings", Baseline)).EnsureSuccessStatusCode();

        var request = new UpdateSettingsRequest(
            BusinessHours: new Dictionary<string, DayHours>
            {
                ["sat"] = new DayHours("10:00", "13:00", Closed: false),
            },
            AppointmentSettings: new AppointmentSettingsDto(
                SlotDurationMinutes: 20, BookingCutoffHours: 3, AutoConfirm: false,
                MaxAdvanceDays: 45, AllowOverbooking: false, ReminderHoursBefore: 12, NoShowGraceMinutes: 10));

        var patch = await client.PatchAsJsonAsync("/api/v1/settings", request);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var updated = await patch.Content.ReadFromJsonAsync<SettingsDto>();
        Assert.NotNull(updated);
        Assert.Equal(20, updated!.AppointmentSettings.SlotDurationMinutes);
        Assert.False(updated.AppointmentSettings.AutoConfirm);
        Assert.Equal("10:00", updated.BusinessHours["sat"].Open);
        Assert.Equal("13:00", updated.BusinessHours["sat"].Close);

        // Re-read confirms the change persisted (and the untouched 'mon' window is intact — partial PATCH).
        var afterGet = await client.GetAsync("/api/v1/settings");
        Assert.Equal(HttpStatusCode.OK, afterGet.StatusCode);
        var after = await afterGet.Content.ReadFromJsonAsync<SettingsDto>();
        Assert.NotNull(after);
        Assert.Equal(20, after!.AppointmentSettings.SlotDurationMinutes);
        Assert.Equal("10:00", after.BusinessHours["sat"].Open);
        Assert.Equal("09:00", after.BusinessHours["mon"].Open);     // untouched section preserved

        // Restore baseline so a later GET (this class) and the demo are not left mutated.
        (await client.PatchAsJsonAsync("/api/v1/settings", Baseline)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Patch_Rejects_Insane_SlotDuration()
    {
        var client = await AuthedClientAsync();
        // 600 minutes is out of the [5,120] range -> validation 422 (no DB write).
        var request = new UpdateSettingsRequest(
            AppointmentSettings: new AppointmentSettingsDto(SlotDurationMinutes: 600));
        var patch = await client.PatchAsJsonAsync("/api/v1/settings", request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, patch.StatusCode);
    }

    [Fact]
    public async Task Get_Without_Token_Is_Unauthorized()
    {
        var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.AdminEmail, DocslotWebAppFactory.AdminPassword, factory.TenantId));
        login.EnsureSuccessStatusCode();
        var token = await login.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }
}
