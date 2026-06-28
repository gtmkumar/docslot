using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using mediq.Application.Abstractions;
using mediq.SharedDataModel.Docslot.Auth;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-2 organic attribution path #2 — BROKER-PORTAL BOOKING. A logged-in broker books on behalf of a
/// referred patient via POST /commission/me/bookings: a behalf booking (patient consent OTP, DPDP) + an
/// auto-verified <c>broker_portal_booking</c> attribution, in one UoW. Consent confirm → booking proceeds;
/// consent deny → booking cancelled + attribution reversed. Drives the real broker-authed endpoint + the real
/// signed WhatsApp webhook for the patient's consent reply, against the live canonical DB.
/// </summary>
public sealed class BrokerPortalBookingTests(BrokerPortalWebAppFactory factory)
    : IClassFixture<BrokerPortalWebAppFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex SixDigits = new(@"\b(\d{6})\b", RegexOptions.Compiled);

    [Fact]
    public async Task BrokerBooks_CreatesBehalfBookingPendingConsent_AutoVerifiedAttribution_CreditsPendingWallet()
    {
        await using var conn = await OpenAsync();
        var slotId = await SeedFutureSlotAsync(conn);
        var patientPhone = NewPhone();
        var before = await WalletAsync(conn);

        var result = await BookAsync(slotId, patientPhone);
        Assert.True(result.StatusCode == HttpStatusCode.OK, $"book failed {(int)result.StatusCode}: {await result.Content.ReadAsStringAsync()}");
        var body = await result.Content.ReadFromJsonAsync<BookResponse>();
        Assert.NotNull(body);
        Assert.Equal("awaiting_patient_consent", body!.Status);

        var booking = await BookingAsync(conn, body.BookingId);
        Assert.Equal("behalf", booking.BookedByType);
        Assert.Equal("care_partner", booking.BehalfRelation);
        Assert.Equal("pending", booking.ConsentStatus);
        Assert.Equal("broker_portal", booking.BookedVia);

        // Consent OTP went to the PATIENT.
        Assert.NotNull(await ConsentOtpTextAsync(conn, patientPhone));

        // Auto-verified attribution (broker demonstrably booked) — pending money until completion.
        var attr = await AttributionAsync(conn, body.BookingId);
        Assert.Equal("broker_portal_booking", attr.Source);
        Assert.Equal("auto_verified", attr.Verification);
        Assert.Equal("pending", attr.Commission);
        Assert.Equal(BrokerPortalWebAppFactory.FlatCommission, attr.Amount);

        Assert.Equal(before.Pending + BrokerPortalWebAppFactory.FlatCommission, (await WalletAsync(conn)).Pending);
    }

    [Fact]
    public async Task BrokerBooks_PatientConfirmsConsent_BookingConfirmed_AttributionStillAutoVerified()
    {
        await using var conn = await OpenAsync();
        var slotId = await SeedFutureSlotAsync(conn);
        var patientPhone = NewPhone();

        var body = await (await BookAsync(slotId, patientPhone)).Content.ReadFromJsonAsync<BookResponse>();
        var code = ExtractCode(await ConsentOtpTextAsync(conn, patientPhone));
        await SendWebhookAsync(patientPhone, code);   // patient approves

        // Consent is confirmed; the booking stays alive (pending staff approval, like the WhatsApp behalf flow).
        var booking = await BookingAsync(conn, body!.BookingId);
        Assert.Equal("confirmed", booking.ConsentStatus);
        Assert.NotEqual("cancelled", booking.Status);

        var attr = await AttributionAsync(conn, body.BookingId);
        Assert.Equal("auto_verified", attr.Verification);
        Assert.Equal("pending", attr.Commission);   // earns later, on booking completion (path #1 machinery)
    }

    [Fact]
    public async Task BrokerBooks_PatientDeniesConsent_CancelsBooking_ReversesAttribution_DebitsWallet()
    {
        await using var conn = await OpenAsync();
        var slotId = await SeedFutureSlotAsync(conn);
        var patientPhone = NewPhone();
        var before = await WalletAsync(conn);

        var body = await (await BookAsync(slotId, patientPhone)).Content.ReadFromJsonAsync<BookResponse>();
        await SendWebhookAsync(patientPhone, "NO");   // patient declines → booking cancelled

        var booking = await BookingAsync(conn, body!.BookingId);
        Assert.Equal("cancelled", booking.Status);
        Assert.Equal("denied", booking.ConsentStatus);

        var attr = await AttributionAsync(conn, body.BookingId);
        Assert.Equal("reversed", attr.Commission);   // the auto-verified attribution was clawed back

        var after = await WalletAsync(conn);
        Assert.Equal(before.Pending, after.Pending);
        Assert.Equal(before.LifetimeReversed + BrokerPortalWebAppFactory.FlatCommission, after.LifetimeReversed);
    }

    [Fact]
    public async Task BrokerBooks_WhenLinkInactive_Rejected403_NoBooking()
    {
        await using var conn = await OpenAsync();
        var slotId = await SeedFutureSlotAsync(conn);
        var patientPhone = NewPhone();

        // Deactivate the broker↔tenant link → the broker may not book for this tenant.
        await Exec(conn, "UPDATE commission.broker_tenant_links SET is_active=false WHERE broker_id=@b AND tenant_id=@t", ("b", factory.BrokerId), ("t", factory.TenantId));
        try
        {
            var resp = await BookAsync(slotId, patientPhone);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            Assert.Equal(0, await ScalarAsync<long>(conn, "SELECT count(*) FROM docslot.bookings WHERE slot_id=@s", ("s", slotId)));
        }
        finally
        {
            await Exec(conn, "UPDATE commission.broker_tenant_links SET is_active=true WHERE broker_id=@b AND tenant_id=@t", ("b", factory.BrokerId), ("t", factory.TenantId));
        }
    }

    [Fact]
    public async Task BrokerBooks_ConsentExpires_SweepCancelsBooking_ReversesAttribution_DebitsWallet()
    {
        await using var conn = await OpenAsync();
        var slotId = await SeedFutureSlotAsync(conn);
        var patientPhone = NewPhone();
        var before = await WalletAsync(conn);

        var body = await (await BookAsync(slotId, patientPhone)).Content.ReadFromJsonAsync<BookResponse>();
        Assert.Equal(before.Pending + BrokerPortalWebAppFactory.FlatCommission, (await WalletAsync(conn)).Pending);

        // Age the consent OTP past its TTL, then run the cross-tenant consent-expiry sweep (the worker tick).
        await Exec(conn, "UPDATE docslot.booking_consent_otps SET expires_at = NOW() - INTERVAL '1 hour' WHERE booking_id=@b AND status='pending'", ("b", body!.BookingId));
        using (var scope = factory.Services.CreateScope())
        {
            var consent = scope.ServiceProvider.GetRequiredService<IConsentOtpStore>();
            Assert.True(await consent.ExpireStaleAsync(default) >= 1, "the sweep should cancel the consent-lapsed booking");
        }

        var booking = await BookingAsync(conn, body.BookingId);
        Assert.Equal("cancelled", booking.Status);
        Assert.Equal("expired", booking.ConsentStatus);

        // The SQL sweep reversed the broker-portal attribution + debited the wallet (byte-parity with the C# path).
        var attr = await AttributionAsync(conn, body.BookingId);
        Assert.Equal("reversed", attr.Commission);
        var after = await WalletAsync(conn);
        Assert.Equal(before.Pending, after.Pending);
        Assert.Equal(before.LifetimeReversed + BrokerPortalWebAppFactory.FlatCommission, after.LifetimeReversed);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private async Task<Guid> SeedFutureSlotAsync(NpgsqlConnection conn)
    {
        var slotId = Guid.NewGuid();
        var start = new TimeOnly(10, Random.Shared.Next(0, 59), Random.Shared.Next(0, 59));
        await Exec(conn, "INSERT INTO docslot.time_slots (slot_id,tenant_id,doctor_id,slot_date,start_time,end_time,status,current_count,max_count,created_at) VALUES (@id,@t,@d,@date,@s,@e,'available',0,1,NOW())",
            ("id", slotId), ("t", factory.TenantId), ("d", factory.DoctorId), ("date", BrokerPortalWebAppFactory.SlotDate), ("s", start), ("e", start.AddMinutes(15)));
        return slotId;
    }

    private async Task<HttpResponseMessage> BookAsync(Guid slotId, string patientPhone)
    {
        var client = await BrokerClientAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/commission/me/bookings")
        {
            Content = JsonContent.Create(new
            {
                patientPhone, patientName = "Portal Patient", patientAge = (short?)null, patientGender = (string?)null,
                slotId, doctorId = factory.DoctorId, departmentId = (Guid?)null, chiefComplaint = (string?)null,
            }),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.SendAsync(req);
    }

    private async Task<HttpClient> BrokerClientAsync()
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.BrokerEmail, BrokerPortalWebAppFactory.Password, factory.TenantId));
        login.EnsureSuccessStatusCode();
        var token = await login.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private async Task SendWebhookAsync(string phone, string text)
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

    private static string Sign(string body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(BrokerPortalWebAppFactory.AppSecret), Encoding.UTF8.GetBytes(body)));

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
                                metadata = new { display_phone_number = "+919700000400", phone_number_id = BrokerPortalWebAppFactory.PhoneNumberId },
                                contacts = new[] { new { wa_id = from, profile = new { name = "Portal Patient" } } },
                                messages = new[] { new { id = messageId, from, type = "text", text = new { body = text } } },
                            },
                        },
                    },
                },
            },
        }, Json);

    private async Task<(string BookedByType, string BehalfRelation, string ConsentStatus, string BookedVia, string Status)> BookingAsync(NpgsqlConnection conn, Guid bookingId)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT booked_by_type, COALESCE(behalf_relation,''), patient_consent_status, booked_via, status FROM docslot.bookings WHERE booking_id=@b", conn);
        cmd.Parameters.AddWithValue("b", bookingId);
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync(), "booking not found");
        return (rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4));
    }

    private async Task<(string Source, string Verification, string Commission, decimal Amount)> AttributionAsync(NpgsqlConnection conn, Guid bookingId)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT attribution_source, verification_status, commission_status, COALESCE(commission_amount_inr,0) FROM commission.attributions WHERE booking_id=@b AND broker_id=@br ORDER BY created_at DESC LIMIT 1", conn);
        cmd.Parameters.AddWithValue("b", bookingId);
        cmd.Parameters.AddWithValue("br", factory.BrokerId);
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync(), "attribution not found");
        return (rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetDecimal(3));
    }

    private async Task<(decimal Pending, decimal Earned, decimal LifetimeReversed)> WalletAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("SELECT pending_inr, earned_inr, lifetime_reversed_inr FROM commission.broker_wallets WHERE broker_id=@b", conn);
        cmd.Parameters.AddWithValue("b", factory.BrokerId);
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync(), "no wallet");
        return (rd.GetDecimal(0), rd.GetDecimal(1), rd.GetDecimal(2));
    }

    private async Task<string?> ConsentOtpTextAsync(NpgsqlConnection conn, string patientPhone)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT payload->>'text' FROM docslot.outbox_messages WHERE tenant_id=@t AND message_intent='consent_otp' AND payload->>'to'=@p ORDER BY created_at DESC LIMIT 1", conn);
        cmd.Parameters.AddWithValue("t", factory.TenantId);
        cmd.Parameters.AddWithValue("p", patientPhone);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    private static string ExtractCode(string? otpText)
    {
        Assert.NotNull(otpText);
        var m = SixDigits.Match(otpText!);
        Assert.True(m.Success, "consent OTP text did not contain a 6-digit code");
        return m.Groups[1].Value;
    }

    private static string NewPhone() => $"9197{Random.Shared.Next(10000000, 99999999)}";

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(BrokerPortalWebAppFactory.AdminConnString);
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

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed record BookResponse(Guid BookingId, string? BookingNumber, Guid AttributionId, string Status);
}
