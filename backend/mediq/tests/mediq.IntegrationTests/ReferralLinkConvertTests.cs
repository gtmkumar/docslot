using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-2 organic attribution path #3 — REFERRAL-LINK click→convert (WhatsApp-first). A broker shares
/// <c>/api/v1/ref/{shortCode}</c>; a click is logged + the visitor is 302-redirected to the clinic's WhatsApp
/// with the code prefilled; the patient's first message carries the code, which the inbound handler detects and
/// uses to attribute the resulting booking to the broker (auto_verified) + mark the click converted. Drives the
/// real public endpoint + the real signed WhatsApp flow against the live canonical DB.
/// </summary>
public sealed class ReferralLinkConvertTests(ReferralLinkWebAppFactory factory)
    : IClassFixture<ReferralLinkWebAppFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PublicClick_RedirectsToWhatsAppDeepLink_AndLogsClick()
    {
        await using var conn = await OpenAsync();
        var before = await ScalarAsync<int>(conn, "SELECT click_count FROM commission.referral_links WHERE link_id=@l", ("l", factory.LinkId));

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.GetAsync($"/api/v1/ref/{factory.ShortCode}");

        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);                 // 302
        var loc = resp.Headers.Location?.ToString() ?? "";
        Assert.StartsWith("https://wa.me/919700000500", loc);                // → the clinic's WhatsApp deep link
        Assert.Contains(factory.ShortCode, loc);                             // with the referral code carried in the prefill

        Assert.Equal(before + 1, await ScalarAsync<int>(conn, "SELECT click_count FROM commission.referral_links WHERE link_id=@l", ("l", factory.LinkId)));
        // A click row exists; the IP is HASHED (never raw) or null on loopback — never a raw dotted IP.
        var ipHash = await ScalarOrNullAsync(conn, "SELECT ip_address_hash FROM commission.referral_clicks WHERE link_id=@l ORDER BY clicked_at DESC LIMIT 1", ("l", factory.LinkId));
        Assert.True(ipHash is null || (ipHash.Length == 64 && !ipHash.Contains('.')), $"ip must be hashed, got '{ipHash}'");
    }

    [Fact]
    public async Task PublicClick_UnknownCode_Returns404()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/api/v1/ref/BRK-00000000");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task WhatsAppBooking_WithReferralCode_AttributesToBroker_MarksConverted_CreditsWallet()
    {
        await using var conn = await OpenAsync();
        var patient = $"9198{Random.Shared.Next(10000000, 99999999)}";
        var walletBefore = await WalletPendingAsync(conn);
        var convBefore = await ScalarAsync<int>(conn, "SELECT conversion_count FROM commission.referral_links WHERE link_id=@l", ("l", factory.LinkId));

        // 1) Patient clicks the broker's link (logs a click to later convert).
        var web = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Found, (await web.GetAsync($"/api/v1/ref/{factory.ShortCode}")).StatusCode);

        // 2) Patient lands in WhatsApp and sends the prefilled message carrying the code, then books for SELF.
        await SendAsync(patient, $"Hi! I'd like to book an appointment. (Ref: {factory.ShortCode})");
        await SendAsync(patient, "1");   // myself → departments
        await SendAsync(patient, "1");   // department → doctors
        await SendAsync(patient, "1");   // doctor → slots
        await SendAsync(patient, "1");   // slot → confirm summary
        var bookingId = await ConfirmAndGetBookingAsync(conn, patient);

        // The booking is attributed to the broker via the referral link (auto_verified, earns on completion).
        var attr = await AttributionAsync(conn, bookingId);
        Assert.Equal("referral_link", attr.Source);
        Assert.Equal("auto_verified", attr.Verification);
        Assert.Equal("pending", attr.Commission);
        Assert.Equal(ReferralLinkWebAppFactory.FlatCommission, attr.Amount);

        // Conversion tracked: count bumped + a click attached to this booking.
        Assert.Equal(convBefore + 1, await ScalarAsync<int>(conn, "SELECT conversion_count FROM commission.referral_links WHERE link_id=@l", ("l", factory.LinkId)));
        Assert.Equal(1, await ScalarAsync<long>(conn, "SELECT count(*) FROM commission.referral_clicks WHERE link_id=@l AND converted_to_booking_id=@b", ("l", factory.LinkId), ("b", bookingId)));

        Assert.Equal(walletBefore + ReferralLinkWebAppFactory.FlatCommission, await WalletPendingAsync(conn));
    }

    [Fact]
    public async Task WhatsAppBooking_ReferralAttributionFails_BookingStillSucceeds_NoMoney()
    {
        await using var conn = await OpenAsync();
        var patient = $"9198{Random.Shared.Next(10000000, 99999999)}";
        var walletBefore = await WalletPendingAsync(conn);

        // Break the broker↔tenant link so CreateAttributionCommand fails — the best-effort + SAVEPOINT path must
        // leave the patient's booking fully committed (a referral failure can never destroy a booking).
        await Exec(conn, "UPDATE commission.broker_tenant_links SET is_active=false WHERE broker_id=@b AND tenant_id=@t", ("b", factory.BrokerId), ("t", factory.TenantId));
        try
        {
            await SendAsync(patient, $"Hi! I'd like to book. (Ref: {factory.ShortCode})");
            await SendAsync(patient, "1");
            await SendAsync(patient, "1");
            await SendAsync(patient, "1");
            await SendAsync(patient, "1");
            var bookingId = await ConfirmAndGetBookingAsync(conn, patient);

            Assert.NotEqual(Guid.Empty, bookingId);   // the booking committed despite the referral failure
            Assert.Equal(0, await ScalarAsync<long>(conn, "SELECT count(*) FROM commission.attributions WHERE booking_id=@b", ("b", bookingId)));
            Assert.Equal(walletBefore, await WalletPendingAsync(conn));   // no commission credited
        }
        finally
        {
            await Exec(conn, "UPDATE commission.broker_tenant_links SET is_active=true WHERE broker_id=@b AND tenant_id=@t", ("b", factory.BrokerId), ("t", factory.TenantId));
        }
    }

    // ---- WhatsApp flow ------------------------------------------------------------------------------

    private async Task SendAsync(string phone, string text)
    {
        var client = factory.CreateClient();
        var body = MessageBody(phone, $"wamid.{factory.TenantId:N}-{Guid.NewGuid():N}", text);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/whatsapp/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Hub-Signature-256", Sign(body));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(req)).StatusCode);
    }

    private async Task<Guid> ConfirmAndGetBookingAsync(NpgsqlConnection conn, string patient)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await SendAsync(patient, "YES");
            var id = await ScalarOrNullGuidAsync(conn,
                """
                SELECT b.booking_id FROM docslot.bookings b JOIN docslot.patients p ON p.patient_id=b.patient_id
                WHERE b.tenant_id=@t AND p.phone_number=@p ORDER BY b.booked_at DESC LIMIT 1
                """, ("t", factory.TenantId), ("p", patient));
            if (id is { } g) return g;
        }
        Assert.Fail("booking was not created after confirming");
        return Guid.Empty;
    }

    private static string Sign(string body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(ReferralLinkWebAppFactory.AppSecret), Encoding.UTF8.GetBytes(body)));

    private static string MessageBody(string from, string messageId, string text) =>
        JsonSerializer.Serialize(new
        {
            @object = "whatsapp_business_account",
            entry = new[]
            {
                new
                {
                    id = "entry-1",
                    changes = new[]
                    {
                        new
                        {
                            field = "messages",
                            value = new
                            {
                                messaging_product = "whatsapp",
                                metadata = new { display_phone_number = "+919700000500", phone_number_id = ReferralLinkWebAppFactory.PhoneNumberId },
                                contacts = new[] { new { wa_id = from, profile = new { name = "Referral Patient" } } },
                                messages = new[] { new { id = messageId, from, type = "text", text = new { body = text } } },
                            },
                        },
                    },
                },
            },
        }, Json);

    // ---- DB reads ------------------------------------------------------------------------------------

    private async Task<(string Source, string Verification, string Commission, decimal Amount)> AttributionAsync(NpgsqlConnection conn, Guid bookingId)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT attribution_source, verification_status, commission_status, COALESCE(commission_amount_inr,0) FROM commission.attributions WHERE booking_id=@b AND broker_id=@br ORDER BY created_at DESC LIMIT 1", conn);
        cmd.Parameters.AddWithValue("b", bookingId);
        cmd.Parameters.AddWithValue("br", factory.BrokerId);
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync(), "no referral attribution found for the booking");
        return (rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetDecimal(3));
    }

    private async Task<decimal> WalletPendingAsync(NpgsqlConnection conn) =>
        await ScalarAsync<decimal>(conn, "SELECT pending_inr FROM commission.broker_wallets WHERE broker_id=@b", ("b", factory.BrokerId));

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(ReferralLinkWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        var r = await cmd.ExecuteScalarAsync();
        Assert.NotNull(r);
        return (T)r!;
    }

    private static async Task<string?> ScalarOrNullAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    private static async Task<Guid?> ScalarOrNullGuidAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        var r = await cmd.ExecuteScalarAsync();
        return r is Guid g ? g : null;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
