using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-1 DPDP behalf-booking patient-consent OTP, driven end-to-end over the live WhatsApp webhook against
/// the canonical DB. A BOOKER number books FOR a different PATIENT number ("someone else" → relation → patient
/// phone → dept → doctor → slot → YES). That persists a behalf booking (booked_by_type='behalf',
/// behalf_relation set, patient_consent_status='pending'), a docslot.booking_consent_otps row, and a
/// consent_otp outbox message TO the patient phone carrying the 6-digit code.
/// <para>
/// The code is never returned by any API (only a salted hash is stored). The test recovers it from the
/// outbox row's <c>payload-&gt;&gt;'text'</c>, then drives the PATIENT's inbound reply through the same signed
/// webhook the inbound tests use. Approve is blocked 422 until consent is 'confirmed'; a wrong code reprompts;
/// 'NO' denies + cancels + frees the slot; exhausting max attempts denies.
/// </para>
/// Each test uses fresh booker/patient numbers so the factory's tenant-scoped cleanup (which also CASCADE-drops
/// booking_consent_otps via bookings) reclaims everything; profile rows are cleaned per-number here.
/// </summary>
public sealed class BehalfConsentOtpTests(WhatsAppWebAppFactory factory) : IClassFixture<WhatsAppWebAppFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex SixDigits = new(@"\b(\d{6})\b", RegexOptions.Compiled);

    [Fact]
    public async Task Behalf_Booking_Persists_Pending_Consent_And_Enqueues_Otp_To_Patient()
    {
        var booker = NewPhone();
        var patient = NewPhone();
        await using var conn = await OpenAsync();

        try
        {
            await DriveBehalfToConfirmStepAsync(booker, patient);

            var (bookingId, bookedByType, relation, consentStatus) = await ReadBehalfBookingAsync(conn, patient);
            Assert.NotEqual(Guid.Empty, bookingId);
            Assert.Equal("behalf", bookedByType);
            Assert.Equal("family", relation);                 // relation option "1" → family
            Assert.Equal("pending", consentStatus);

            // A pending consent OTP row exists for the PATIENT number, stamped to this booking.
            var otpCount = await ScalarAsync<long>(conn,
                "SELECT count(*) FROM docslot.booking_consent_otps WHERE tenant_id=@t AND patient_phone=@p AND booking_id=@b AND status='pending'",
                ("t", factory.TenantId), ("p", patient), ("b", bookingId));
            Assert.Equal(1, otpCount);

            // The OTP code was enqueued to the PATIENT number (not the booker) as a consent_otp outbox message.
            var otpText = await OtpOutboxTextAsync(conn, patient);
            Assert.NotNull(otpText);
            Assert.Matches(SixDigits, otpText!);
        }
        finally { await CleanupNumbersAsync(conn, booker, patient); }
    }

    [Fact]
    public async Task Approve_Is_Blocked_422_While_Consent_Not_Confirmed_Then_Succeeds_After_Confirm()
    {
        var booker = NewPhone();
        var patient = NewPhone();
        await using var conn = await OpenAsync();

        try
        {
            await DriveBehalfToConfirmStepAsync(booker, patient);
            var (bookingId, _, _, _) = await ReadBehalfBookingAsync(conn, patient);

            // Approve via the authed Dashboard API is BLOCKED (422) while consent is still pending.
            var client = await AuthedClientAsync();
            var blocked = await PostAsync(client, $"/api/v1/bookings/{bookingId}/approve", Guid.NewGuid().ToString(), new { });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);

            // Patient replies with the correct code → consent confirmed.
            var code = ExtractCode(await OtpOutboxTextAsync(conn, patient));
            await SendAsync(patient, code);

            var consentStatus = await ScalarAsync<string>(conn,
                "SELECT patient_consent_status FROM docslot.bookings WHERE booking_id=@b", ("b", bookingId));
            Assert.Equal("confirmed", consentStatus);

            // Now Approve succeeds.
            var approve = await PostAsync(client, $"/api/v1/bookings/{bookingId}/approve", Guid.NewGuid().ToString(), new { });
            Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
            var status = await ScalarAsync<string>(conn,
                "SELECT status FROM docslot.bookings WHERE booking_id=@b", ("b", bookingId));
            Assert.Equal("confirmed", status);
        }
        finally { await CleanupNumbersAsync(conn, booker, patient); }
    }

    [Fact]
    public async Task Wrong_Code_Reprompts_And_Leaves_Consent_Pending()
    {
        var booker = NewPhone();
        var patient = NewPhone();
        await using var conn = await OpenAsync();

        try
        {
            await DriveBehalfToConfirmStepAsync(booker, patient);
            var (bookingId, _, _, _) = await ReadBehalfBookingAsync(conn, patient);

            // A 6-digit value that is NOT the real code (real code derived; flip a digit to be safe).
            var realCode = ExtractCode(await OtpOutboxTextAsync(conn, patient));
            var wrong = WrongVariant(realCode);
            await SendAsync(patient, wrong);

            // Consent stays pending; the attempt counter advanced; booking is NOT cancelled.
            var (status, consent, attempts) = await ReadOtpAndBookingAsync(conn, bookingId, patient);
            Assert.Equal("pending", consent);
            Assert.Equal("pending", status);          // booking still pending (not cancelled)
            Assert.True(attempts >= 1, "a wrong code should have incremented attempts");
        }
        finally { await CleanupNumbersAsync(conn, booker, patient); }
    }

    [Fact]
    public async Task Decline_Reply_NO_Denies_Consent_Cancels_Booking_And_Frees_Slot()
    {
        var booker = NewPhone();
        var patient = NewPhone();
        await using var conn = await OpenAsync();

        try
        {
            await DriveBehalfToConfirmStepAsync(booker, patient);
            var (bookingId, _, _, _) = await ReadBehalfBookingAsync(conn, patient);
            var slotId = await ScalarAsync<Guid>(conn, "SELECT slot_id FROM docslot.bookings WHERE booking_id=@b", ("b", bookingId));

            // Patient declines.
            await SendAsync(patient, "NO");

            var bookingStatus = await ScalarAsync<string>(conn, "SELECT status FROM docslot.bookings WHERE booking_id=@b", ("b", bookingId));
            var consent = await ScalarAsync<string>(conn, "SELECT patient_consent_status FROM docslot.bookings WHERE booking_id=@b", ("b", bookingId));
            var otpStatus = await ScalarAsync<string>(conn,
                "SELECT status FROM docslot.booking_consent_otps WHERE booking_id=@b ORDER BY created_at DESC LIMIT 1", ("b", bookingId));
            Assert.Equal("cancelled", bookingStatus);
            Assert.Equal("denied", consent);
            Assert.Equal("denied", otpStatus);

            // The slot capacity is freed (re-bookable): current_count back to 0, status back to 'available'.
            var (count, slotStatus) = await SlotStateAsync(conn, slotId);
            Assert.Equal(0, count);
            Assert.Equal("available", slotStatus);
        }
        finally { await CleanupNumbersAsync(conn, booker, patient); }
    }

    [Fact]
    public async Task Exhausting_Max_Attempts_Denies_Consent_And_Cancels_Booking()
    {
        var booker = NewPhone();
        var patient = NewPhone();
        await using var conn = await OpenAsync();

        try
        {
            await DriveBehalfToConfirmStepAsync(booker, patient);
            var (bookingId, _, _, _) = await ReadBehalfBookingAsync(conn, patient);

            var realCode = ExtractCode(await OtpOutboxTextAsync(conn, patient));
            var wrong = WrongVariant(realCode);
            var maxAttempts = await ScalarAsync<short>(conn,
                "SELECT max_attempts FROM docslot.booking_consent_otps WHERE booking_id=@b ORDER BY created_at DESC LIMIT 1", ("b", bookingId));

            // Send the wrong code max_attempts times → the last one exhausts attempts and denies.
            for (var i = 0; i < maxAttempts; i++)
                await SendAsync(patient, wrong);

            var bookingStatus = await ScalarAsync<string>(conn, "SELECT status FROM docslot.bookings WHERE booking_id=@b", ("b", bookingId));
            var consent = await ScalarAsync<string>(conn, "SELECT patient_consent_status FROM docslot.bookings WHERE booking_id=@b", ("b", bookingId));
            var otpStatus = await ScalarAsync<string>(conn,
                "SELECT status FROM docslot.booking_consent_otps WHERE booking_id=@b ORDER BY created_at DESC LIMIT 1", ("b", bookingId));
            Assert.Equal("cancelled", bookingStatus);
            Assert.Equal("denied", consent);
            Assert.Equal("failed", otpStatus);   // exhausted attempts mark the OTP 'failed'
        }
        finally { await CleanupNumbersAsync(conn, booker, patient); }
    }

    // ---- flow drivers --------------------------------------------------------------------------------

    /// <summary>
    /// Drives the booker number through the behalf path up to and including the confirming "YES", which creates
    /// the behalf booking and dispatches the OTP. Resends "YES" only while no behalf booking exists yet — the
    /// same transient-retry pattern the inbound tests use (the global audit-chain advisory lock can transiently
    /// fail the first confirm under parallel load; a real logic break fails every attempt).
    /// </summary>
    private async Task DriveBehalfToConfirmStepAsync(string booker, string patient)
    {
        await SendAsync(booker, "hi");          // greeting → who-for
        await SendAsync(booker, "2");           // someone else → ask relation
        await SendAsync(booker, "1");           // relation 1 = family → ask patient phone
        await SendAsync(booker, patient);       // patient's number → departments
        await SendAsync(booker, "1");           // department 1 (Cardiology) → doctors
        await SendAsync(booker, "1");           // doctor 1 → slots
        await SendAsync(booker, "1");           // slot 1 → confirm summary

        await using var conn = await OpenAsync();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await SendAsync(booker, "YES");
            var exists = await ScalarAsync<long>(conn,
                "SELECT count(*) FROM docslot.bookings WHERE tenant_id=@t AND booked_by_type='behalf' AND patient_phone_at_booking=@p",
                ("t", factory.TenantId), ("p", patient));
            if (exists > 0) return;
        }
        Assert.Fail("behalf booking was not created after confirming");
    }

    // ---- webhook plumbing (mirrors WhatsAppInboundTests) ---------------------------------------------

    private async Task SendAsync(string phone, string text)
    {
        var client = factory.CreateClient();
        var resp = await PostSignedAsync(client, MessageBody(phone, NewMessageId(), text));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private async Task<HttpResponseMessage> PostSignedAsync(HttpClient client, string body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/whatsapp/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Hub-Signature-256", Sign(body));
        return await client.SendAsync(req);
    }

    private static string Sign(string body)
    {
        var key = Encoding.UTF8.GetBytes(WhatsAppWebAppFactory.AppSecret);
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    private string NewMessageId() => $"wamid.{factory.TenantId:N}-{Guid.NewGuid():N}";

    private static string NewPhone() => $"9198{Random.Shared.Next(10000000, 99999999)}";

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
                                metadata = new { display_phone_number = "+919000000000", phone_number_id = WhatsAppWebAppFactory.PhoneNumberId },
                                contacts = new[] { new { wa_id = from, profile = new { name = "WA Tester" } } },
                                messages = new[]
                                {
                                    new { id = messageId, from, type = "text", text = new { body = text } },
                                },
                            },
                        },
                    },
                },
            },
        }, Json);

    // ---- DB helpers ----------------------------------------------------------------------------------

    private static Task<NpgsqlConnection> OpenAsync() => OpenAsync(WhatsAppWebAppFactory.ConnectionString);

    private static async Task<NpgsqlConnection> OpenAsync(string cs)
    {
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<HttpClient> AuthedClientAsync()
    {
        // The WhatsApp fixture seeds an anonymous tenant with no admin user; create a tenant_owner admin for
        // it on demand so the Dashboard approve path is callable. Cleaned up by the per-number cleanup +
        // factory tenant cleanup (user rows are keyed to this tenant via user_tenant_roles).
        var email = $"behalf.admin+{Guid.NewGuid():N}@docslot.test";
        var userId = Guid.NewGuid();
        await using var conn = await OpenAsync();
        await Exec(conn,
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, is_platform_user, created_at, updated_at)
            VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Behalf Admin', true, true, NOW(), NOW())
            ON CONFLICT (email) DO UPDATE SET password_hash = EXCLUDED.password_hash, deleted_at = NULL
            """,
            ("id", userId), ("email", email), ("pwd", AdminPassword));
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key = 'tenant_owner' AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", userId), ("tid", factory.TenantId));

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new mediq.SharedDataModel.Docslot.Auth.LoginRequest(email, AdminPassword, factory.TenantId));
        login.EnsureSuccessStatusCode();
        var token = await login.Content.ReadFromJsonAsync<mediq.SharedDataModel.Docslot.Auth.TokenResponse>();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private const string AdminPassword = "Sup3rSecret!";

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string idempotencyKey, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }

    private async Task<(Guid BookingId, string BookedByType, string Relation, string Consent)> ReadBehalfBookingAsync(
        NpgsqlConnection conn, string patientPhone)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT booking_id, booked_by_type, behalf_relation, patient_consent_status
            FROM docslot.bookings
            WHERE tenant_id=@t AND booked_by_type='behalf' AND patient_phone_at_booking=@p
            ORDER BY booked_at DESC LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue("t", factory.TenantId);
        cmd.Parameters.AddWithValue("p", patientPhone);
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync(), "no behalf booking found for patient");
        return (rd.GetGuid(0), rd.GetString(1), rd.GetString(2), rd.GetString(3));
    }

    private async Task<string?> OtpOutboxTextAsync(NpgsqlConnection conn, string patientPhone)
    {
        await using var cmd = new NpgsqlCommand(
            """
            SELECT payload->>'text'
            FROM docslot.outbox_messages
            WHERE tenant_id=@t AND message_intent='consent_otp' AND payload->>'to'=@p
            ORDER BY created_at DESC LIMIT 1
            """, conn);
        cmd.Parameters.AddWithValue("t", factory.TenantId);
        cmd.Parameters.AddWithValue("p", patientPhone);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    private async Task<(string BookingStatus, string Consent, short Attempts)> ReadOtpAndBookingAsync(
        NpgsqlConnection conn, Guid bookingId, string patientPhone)
    {
        var bookingStatus = await ScalarAsync<string>(conn, "SELECT status FROM docslot.bookings WHERE booking_id=@b", ("b", bookingId));
        var consent = await ScalarAsync<string>(conn, "SELECT patient_consent_status FROM docslot.bookings WHERE booking_id=@b", ("b", bookingId));
        var attempts = await ScalarAsync<short>(conn,
            "SELECT attempts FROM docslot.booking_consent_otps WHERE booking_id=@b ORDER BY created_at DESC LIMIT 1", ("b", bookingId));
        return (bookingStatus, consent, attempts);
    }

    private static async Task<(int Count, string Status)> SlotStateAsync(NpgsqlConnection conn, Guid slotId)
    {
        await using var cmd = new NpgsqlCommand("SELECT current_count, status FROM docslot.time_slots WHERE slot_id=@id", conn);
        cmd.Parameters.AddWithValue("id", slotId);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        return (rd.GetInt16(0), rd.GetString(1));
    }

    private static string ExtractCode(string? otpText)
    {
        Assert.NotNull(otpText);
        var m = SixDigits.Match(otpText!);
        Assert.True(m.Success, "OTP text did not contain a 6-digit code");
        return m.Groups[1].Value;
    }

    private static string WrongVariant(string code)
    {
        // Deterministically produce a DIFFERENT 6-digit string (rotate the first digit).
        var first = (char)('0' + ((code[0] - '0' + 1) % 10));
        return first + code[1..];
    }

    private async Task CleanupNumbersAsync(NpgsqlConnection conn, params string[] phones)
    {
        foreach (var phone in phones)
        {
            await Exec(conn, "DELETE FROM docslot.wa_message_log WHERE conversation_id IN (SELECT conversation_id FROM docslot.conversations WHERE whatsapp_phone=@p)", ("p", phone));
            await Exec(conn, "DELETE FROM docslot.conversations WHERE whatsapp_phone=@p", ("p", phone));
            await Exec(conn, "DELETE FROM docslot.wa_contact_profiles WHERE phone=@p", ("p", phone));
        }
        // Behalf-admin users created on demand for the approve path (keyed to this tenant via roles). The
        // approve writes an audit_log row referencing the user, and audit_log must NEVER be deleted, so the
        // user is SOFT-deleted + anonymized (mirroring DocslotWebAppFactory) rather than hard-deleted — a
        // hard DELETE would violate audit_log_user_id_fkey.
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id IN (SELECT user_id FROM platform.user_tenant_roles WHERE tenant_id=@t)", ("t", factory.TenantId));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email LIKE 'behalf.admin+%@docslot.test'");
        await Exec(conn,
            "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = 'deleted+' || user_id || '@behalf.test' WHERE user_id IN (SELECT user_id FROM platform.user_tenant_roles WHERE tenant_id=@t) AND deleted_at IS NULL",
            ("t", factory.TenantId));
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE tenant_id=@t", ("t", factory.TenantId));

        // Free the seeded factory slots + consent rows so a later test in the same fixture re-runs cleanly.
        await Exec(conn, "DELETE FROM docslot.booking_consent_otps WHERE tenant_id=@t", ("t", factory.TenantId));
        await Exec(conn, "DELETE FROM docslot.outbox_messages WHERE tenant_id=@t", ("t", factory.TenantId));
        await Exec(conn, "DELETE FROM docslot.slot_holds WHERE tenant_id=@t", ("t", factory.TenantId));
        await Exec(conn, "DELETE FROM docslot.opd_tokens WHERE tenant_id=@t", ("t", factory.TenantId));
        await Exec(conn, "DELETE FROM docslot.booking_status_history WHERE booking_id IN (SELECT booking_id FROM docslot.bookings WHERE tenant_id=@t)", ("t", factory.TenantId));
        await Exec(conn, "DELETE FROM docslot.bookings WHERE tenant_id=@t", ("t", factory.TenantId));
        await Exec(conn, "UPDATE docslot.time_slots SET current_count=0, status='available' WHERE tenant_id=@t", ("t", factory.TenantId));
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        var result = await cmd.ExecuteScalarAsync();
        Assert.NotNull(result);
        return (T)result!;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
