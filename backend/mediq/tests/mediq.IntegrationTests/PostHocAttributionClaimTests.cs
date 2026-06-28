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
/// Phase-2 organic attribution path #1 — the POST-HOC CLAIM. A broker claims a completed booking ("I referred
/// this patient"); we mint a 'post_hoc_claim' attribution (pending, no money earns yet) + send the patient an
/// OTP. The patient CONFIRMS (→ earns) or DENIES (→ reversed); no reply within TTL lapses (sweep → no_response
/// + reversed). Drives the real claim endpoint (authed) + the real signed WhatsApp webhook for the patient
/// reply, against the live canonical DB. Asserts the money moves through the broker wallet correctly and that
/// the discount↔attribution exclusivity + the consent/claim single-live-OTP guard hold.
/// </summary>
public sealed class PostHocAttributionClaimTests(ClaimAttributionWebAppFactory factory)
    : IClassFixture<ClaimAttributionWebAppFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex SixDigits = new(@"\b(\d{6})\b", RegexOptions.Compiled);

    [Fact]
    public async Task Claim_OnCompletedBooking_MintsPendingAttribution_SendsOtp_CreditsPendingWallet()
    {
        await using var conn = await OpenAsync();
        var (bookingId, patientPhone, _) = await SeedCompletedBookingAsync(conn);
        var before = await WalletAsync(conn);

        var resp = await ClaimAsync(bookingId);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var attr = await AttributionAsync(conn, bookingId);
        Assert.Equal("post_hoc_claim", attr.Source);
        Assert.Equal("pending", attr.Verification);
        Assert.Equal("pending", attr.Commission);
        Assert.Equal(ClaimAttributionWebAppFactory.FlatCommission, attr.Amount);

        // OTP sent to the PATIENT as a claim_otp outbox message carrying a 6-digit code; the JOURNAL is redacted.
        var otpText = await ClaimOtpTextAsync(conn, patientPhone);
        Assert.NotNull(otpText);
        Assert.Matches(SixDigits, otpText!);
        var journal = await ScalarOrNullAsync<string>(conn,
            "SELECT content->>'text' FROM docslot.wa_message_log WHERE tenant_id=@t AND direction='outbound' AND content->>'text' LIKE '%referral confirmation code%' ORDER BY sent_at DESC LIMIT 1",
            ("t", factory.TenantId));
        Assert.NotNull(journal);
        Assert.DoesNotMatch(SixDigits, journal!);   // the live code never lands in the journal

        // Pending wallet credited by the commission amount.
        var after = await WalletAsync(conn);
        Assert.Equal(before.Pending + ClaimAttributionWebAppFactory.FlatCommission, after.Pending);
    }

    [Fact]
    public async Task Claim_PatientConfirms_EarnsAttribution_MovesWalletPendingToEarned()
    {
        await using var conn = await OpenAsync();
        var (bookingId, patientPhone, _) = await SeedCompletedBookingAsync(conn);
        var before = await WalletAsync(conn);

        Assert.Equal(HttpStatusCode.OK, (await ClaimAsync(bookingId)).StatusCode);
        var code = ExtractCode(await ClaimOtpTextAsync(conn, patientPhone));

        await SendWebhookAsync(patientPhone, code);   // patient replies with the correct code → confirm

        var attr = await AttributionAsync(conn, bookingId);
        Assert.Equal("patient_confirmed", attr.Verification);
        Assert.Equal("earned", attr.Commission);   // booking already completed → earns on confirm

        var after = await WalletAsync(conn);
        Assert.Equal(before.Earned + ClaimAttributionWebAppFactory.FlatCommission, after.Earned);
        Assert.Equal(before.Pending, after.Pending);   // pending credited then moved out → net zero
    }

    [Fact]
    public async Task Claim_PatientDenies_ReversesAttribution_DebitsWallet()
    {
        await using var conn = await OpenAsync();
        var (bookingId, patientPhone, _) = await SeedCompletedBookingAsync(conn);
        var before = await WalletAsync(conn);

        Assert.Equal(HttpStatusCode.OK, (await ClaimAsync(bookingId)).StatusCode);
        await SendWebhookAsync(patientPhone, "NO");   // patient declines

        var attr = await AttributionAsync(conn, bookingId);
        Assert.Equal("patient_denied", attr.Verification);
        Assert.Equal("reversed", attr.Commission);

        var after = await WalletAsync(conn);
        Assert.Equal(before.Pending, after.Pending);    // the phantom pending credit was reversed
        Assert.Equal(before.LifetimeReversed + ClaimAttributionWebAppFactory.FlatCommission, after.LifetimeReversed);
    }

    [Fact]
    public async Task Claim_NoResponse_SweepReverses_AndDebitsWallet()
    {
        await using var conn = await OpenAsync();
        var (bookingId, _, _) = await SeedCompletedBookingAsync(conn);
        var before = await WalletAsync(conn);

        Assert.Equal(HttpStatusCode.OK, (await ClaimAsync(bookingId)).StatusCode);

        // Age the claim OTP past its TTL, then run the cross-tenant sweep (the worker tick).
        await Exec(conn, "UPDATE commission.attribution_claim_otps SET expires_at = NOW() - INTERVAL '1 hour' WHERE booking_id=@b AND status='pending'", ("b", bookingId));
        using (var scope = factory.Services.CreateScope())
        {
            var claims = scope.ServiceProvider.GetRequiredService<IAttributionClaimOtpStore>();
            var lapsed = await claims.ExpireStaleAsync(default);
            Assert.True(lapsed >= 1, "the sweep should have lapsed the unanswered claim");
        }

        var attr = await AttributionAsync(conn, bookingId);
        Assert.Equal("no_response", attr.Verification);
        Assert.Equal("reversed", attr.Commission);

        var after = await WalletAsync(conn);
        Assert.Equal(before.Pending, after.Pending);
        Assert.Equal(before.LifetimeReversed + ClaimAttributionWebAppFactory.FlatCommission, after.LifetimeReversed);
    }

    [Fact]
    public async Task Claim_OnDiscountedBooking_Rejected422_NoOtp_NoAttribution()
    {
        await using var conn = await OpenAsync();
        var (bookingId, patientPhone, _) = await SeedCompletedBookingAsync(conn, directDiscountInr: 100m);

        var resp = await ClaimAsync(bookingId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);   // discount↔attribution exclusivity

        Assert.Equal(0, await ScalarAsync<long>(conn, "SELECT count(*) FROM commission.attributions WHERE booking_id=@b", ("b", bookingId)));
        Assert.Equal(0, await ScalarAsync<long>(conn, "SELECT count(*) FROM commission.attribution_claim_otps WHERE booking_id=@b", ("b", bookingId)));
        Assert.Equal(0, await ScalarAsync<long>(conn, "SELECT count(*) FROM docslot.outbox_messages WHERE tenant_id=@t AND message_intent='claim_otp' AND payload->>'to'=@p", ("t", factory.TenantId), ("p", patientPhone)));
    }

    [Fact]
    public async Task Claim_WhilePatientHasPendingConsentOtp_Rejected422()
    {
        await using var conn = await OpenAsync();
        var (bookingId, patientPhone, _) = await SeedCompletedBookingAsync(conn);

        // A live behalf-consent OTP exists for this patient → a claim OTP must NOT overlay it (single-live guard).
        await Exec(conn,
            """
            INSERT INTO docslot.booking_consent_otps (consent_otp_id, tenant_id, booking_id, patient_phone, booker_phone, relation, code_salt, code_hash, status, attempts, max_attempts, expires_at, created_at)
            VALUES (gen_random_uuid(), @t, @b, @p, @bk, 'family', 'salt', 'hash', 'pending', 0, 5, NOW() + INTERVAL '10 min', NOW())
            """,
            ("t", factory.TenantId), ("b", bookingId), ("p", patientPhone), ("bk", "919800000000"));

        var resp = await ClaimAsync(bookingId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal(0, await ScalarAsync<long>(conn, "SELECT count(*) FROM commission.attributions WHERE booking_id=@b", ("b", bookingId)));
    }

    [Fact]
    public async Task ConsentOtp_ForPatientWithPendingClaim_SupersedesAndReversesTheClaim()
    {
        await using var conn = await OpenAsync();
        var (bookingId, patientPhone, _) = await SeedCompletedBookingAsync(conn);
        var before = await WalletAsync(conn);

        // Broker files a post-hoc claim → patient has a pending claim OTP + pending attribution (+₹200 pending).
        Assert.Equal(HttpStatusCode.OK, (await ClaimAsync(bookingId)).StatusCode);
        Assert.Equal(before.Pending + ClaimAttributionWebAppFactory.FlatCommission, (await WalletAsync(conn)).Pending);

        // A behalf booking is then made FOR that patient (consent OTP goes to them) → the single-live-OTP guard
        // supersedes the claim so the patient can never have two pending OTPs.
        var booker = $"9196{Random.Shared.Next(10000000, 99999999)}";
        await DriveBehalfBookingAsync(conn, booker, patientPhone);

        var attr = await AttributionAsync(conn, bookingId);
        Assert.Equal("reversed", attr.Commission);   // the claim was superseded → reversed (no phantom credit)

        var after = await WalletAsync(conn);
        Assert.Equal(before.Pending, after.Pending);
        Assert.Equal(before.LifetimeReversed + ClaimAttributionWebAppFactory.FlatCommission, after.LifetimeReversed);

        // The claim OTP is no longer pending; the patient now has exactly one live OTP (the consent).
        Assert.Equal(0, await ScalarAsync<long>(conn,
            "SELECT count(*) FROM commission.attribution_claim_otps WHERE booking_id=@b AND status='pending'", ("b", bookingId)));
    }

    // ---- arrangement helpers -------------------------------------------------------------------------

    private async Task<(Guid BookingId, string PatientPhone, Guid PatientId)> SeedCompletedBookingAsync(
        NpgsqlConnection conn, decimal directDiscountInr = 0m)
    {
        var bookingId = Guid.NewGuid();
        var slotId = Guid.NewGuid();
        var patientId = Guid.NewGuid();
        var patientPhone = $"9197{Random.Shared.Next(10000000, 99999999)}";
        var start = new TimeOnly(9, Random.Shared.Next(0, 59), Random.Shared.Next(0, 59));

        await Exec(conn, "INSERT INTO docslot.time_slots (slot_id,tenant_id,doctor_id,slot_date,start_time,end_time,status,current_count,max_count,created_at) VALUES (@id,@t,@d,CURRENT_DATE,@s,@e,'booked',1,1,NOW())",
            ("id", slotId), ("t", factory.TenantId), ("d", factory.DoctorId), ("s", start), ("e", start.AddMinutes(10)));
        await Exec(conn, "INSERT INTO docslot.patients (patient_id,phone_number,full_name,is_active,created_at,updated_at) VALUES (@id,@p,'Claim Patient',true,NOW(),NOW()) ON CONFLICT (phone_number) DO NOTHING",
            ("id", patientId), ("p", patientPhone));
        await Exec(conn, "INSERT INTO docslot.bookings (booking_id,tenant_id,slot_id,patient_id,doctor_id,status,booked_via,booked_for,direct_discount_inr,booked_at,updated_at,completed_at) VALUES (@id,@t,@s,@p,@d,'completed','dashboard','self',@disc,NOW(),NOW(),NOW())",
            ("id", bookingId), ("t", factory.TenantId), ("s", slotId), ("p", patientId), ("d", factory.DoctorId), ("disc", directDiscountInr));
        return (bookingId, patientPhone, patientId);
    }

    /// <summary>Drives a fresh booker number through the behalf WA flow up to a created behalf booking for the patient (which dispatches the consent OTP).</summary>
    private async Task DriveBehalfBookingAsync(NpgsqlConnection conn, string booker, string patientPhone)
    {
        await SendWebhookAsync(booker, "hi");          // greeting → who-for
        await SendWebhookAsync(booker, "2");           // someone else → relation
        await SendWebhookAsync(booker, "1");           // relation family → ask patient phone
        await SendWebhookAsync(booker, patientPhone);  // patient's number → departments
        await SendWebhookAsync(booker, "1");           // department 1 → doctors
        await SendWebhookAsync(booker, "1");           // doctor 1 → slots
        await SendWebhookAsync(booker, "1");           // slot 1 → confirm summary

        // Confirm; retry only while no behalf booking exists yet (the audit-chain advisory lock can transiently
        // fail the first confirm under parallel load — the same pattern the consent tests use).
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await SendWebhookAsync(booker, "YES");
            var exists = await ScalarAsync<long>(conn,
                "SELECT count(*) FROM docslot.bookings WHERE tenant_id=@t AND booked_by_type='behalf' AND patient_phone_at_booking=@p",
                ("t", factory.TenantId), ("p", patientPhone));
            if (exists > 0) return;
        }
        Assert.Fail("behalf booking was not created after confirming");
    }

    private async Task<HttpResponseMessage> ClaimAsync(Guid bookingId)
    {
        var client = await AuthedClientAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/commission/bookings/{bookingId}/claim-attribution")
        {
            Content = JsonContent.Create(new { brokerId = factory.BrokerId, claimedRelation = (string?)null }),
        };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await client.SendAsync(req);
    }

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.AdminEmail, ClaimAttributionWebAppFactory.Password, factory.TenantId));
        login.EnsureSuccessStatusCode();
        var token = await login.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    // ---- webhook (signed inbound reply) --------------------------------------------------------------

    private async Task SendWebhookAsync(string phone, string text)
    {
        var client = factory.CreateClient();
        var body = MessageBody(phone, $"wamid.{factory.TenantId:N}-{Guid.NewGuid():N}", text);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/whatsapp/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Hub-Signature-256", Sign(body));
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static string Sign(string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(ClaimAttributionWebAppFactory.AppSecret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

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
                                metadata = new { display_phone_number = "+919700000300", phone_number_id = ClaimAttributionWebAppFactory.PhoneNumberId },
                                contacts = new[] { new { wa_id = from, profile = new { name = "Claim Patient" } } },
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
        Assert.True(await rd.ReadAsync(), "no attribution found for the booking");
        return (rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetDecimal(3));
    }

    private async Task<(decimal Pending, decimal Earned, decimal LifetimeReversed)> WalletAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT pending_inr, earned_inr, lifetime_reversed_inr FROM commission.broker_wallets WHERE broker_id=@b", conn);
        cmd.Parameters.AddWithValue("b", factory.BrokerId);
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync(), "no broker wallet");
        return (rd.GetDecimal(0), rd.GetDecimal(1), rd.GetDecimal(2));
    }

    private async Task<string?> ClaimOtpTextAsync(NpgsqlConnection conn, string patientPhone) =>
        await ScalarOrNullAsync<string>(conn,
            "SELECT payload->>'text' FROM docslot.outbox_messages WHERE tenant_id=@t AND message_intent='claim_otp' AND payload->>'to'=@p ORDER BY created_at DESC LIMIT 1",
            ("t", factory.TenantId), ("p", patientPhone));

    private static string ExtractCode(string? otpText)
    {
        Assert.NotNull(otpText);
        var m = SixDigits.Match(otpText!);
        Assert.True(m.Success, "claim OTP text did not contain a 6-digit code");
        return m.Groups[1].Value;
    }

    private static Task<NpgsqlConnection> OpenAsync() => OpenAsync(ClaimAttributionWebAppFactory.AdminConnString);

    private static async Task<NpgsqlConnection> OpenAsync(string cs)
    {
        var conn = new NpgsqlConnection(cs);
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

    private static async Task<T?> ScalarOrNullAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps) where T : class
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (await cmd.ExecuteScalarAsync()) as T;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
