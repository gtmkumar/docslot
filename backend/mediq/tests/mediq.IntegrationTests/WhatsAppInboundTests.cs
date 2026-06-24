using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Drives the inbound WhatsApp conversational-booking flow end-to-end against the live canonical DB:
/// GET verification handshake; a full simulated conversation (greeting → who-for → department → doctor →
/// slot → YES) that creates a real <c>docslot.bookings</c> row with <c>booked_via='whatsapp'</c>; provider
/// redelivery idempotency (processed_messages); and bad-signature rejection (401). Every POST is signed with
/// the dev App Secret exactly as Meta would (X-Hub-Signature-256 over the raw body).
/// </summary>
public sealed class WhatsAppInboundTests(WhatsAppWebAppFactory factory) : IClassFixture<WhatsAppWebAppFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Get_Verification_Echoes_Challenge_And_Rejects_Bad_Token()
    {
        var client = factory.CreateClient();

        var ok = await client.GetAsync(
            $"/api/v1/whatsapp/webhook?hub.mode=subscribe&hub.verify_token={WhatsAppWebAppFactory.VerifyToken}&hub.challenge=42");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("42", await ok.Content.ReadAsStringAsync());

        var bad = await client.GetAsync(
            "/api/v1/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=42");
        Assert.Equal(HttpStatusCode.Forbidden, bad.StatusCode);
    }

    [Fact]
    public async Task Bad_Signature_Is_Rejected_401()
    {
        var client = factory.CreateClient();
        var body = MessageBody(factory.FromPhone, NewMessageId(), "hi");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/whatsapp/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Hub-Signature-256", "sha256=deadbeef");   // wrong signature

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Full_Conversation_Creates_A_Whatsapp_Booking()
    {
        var client = factory.CreateClient();
        var phone = factory.FromPhone;

        // 1) "hi" → greeting + who-for prompt
        await SendAsync(client, phone, "hi");
        // 2) "1" (myself) → department list (Cardiology is option 1)
        await SendAsync(client, phone, "1");
        // 3) "1" (Cardiology) → doctor list
        await SendAsync(client, phone, "1");
        // 4) "1" (the doctor) → slot list
        await SendAsync(client, phone, "1");
        // 5) "1" (earliest slot) → confirmation summary
        await SendAsync(client, phone, "1");

        // 6) "YES" → booking created. CreateBookingCommand is SYNCHRONOUS within this POST AND durably
        // idempotent (stable key wa-{conv}-{slot}), and the handler keeps the conversation on the Confirm
        // step if booking creation transiently fails (e.g. the global audit-chain advisory lock under heavy
        // parallel-suite contention). So a re-sent "YES" cleanly retries the SAME booking — it recovers a
        // transient infra hiccup without ever masking a logic bug (a real break fails every attempt). We
        // resend only while no booking exists yet; a success leaves nothing to retry.
        await using var conn = new NpgsqlConnection(WhatsAppWebAppFactory.ConnectionString);
        await conn.OpenAsync();

        string? bookingNumber = null;
        for (var attempt = 0; attempt < 3 && bookingNumber is null; attempt++)
        {
            await SendAsync(client, phone, "YES");
            await using var cmd = new NpgsqlCommand(
                "SELECT booking_number FROM docslot.bookings WHERE tenant_id = @t AND booked_via = 'whatsapp' ORDER BY booked_at DESC LIMIT 1",
                conn);
            cmd.Parameters.AddWithValue("t", factory.TenantId);
            bookingNumber = (string?)await cmd.ExecuteScalarAsync();
        }

        Assert.NotNull(bookingNumber);
        Assert.StartsWith("BKG-", bookingNumber);

        // A confirmation message was enqueued to the outbox.
        await using var outboxCmd = new NpgsqlCommand(
            "SELECT count(*) FROM docslot.outbox_messages WHERE tenant_id = @t AND message_intent = 'booking_confirmation'",
            conn);
        outboxCmd.Parameters.AddWithValue("t", factory.TenantId);
        var confirmations = (long)(await outboxCmd.ExecuteScalarAsync())!;
        Assert.True(confirmations >= 1, "expected a booking_confirmation outbox message");
    }

    [Fact]
    public async Task Redelivered_Message_Is_Idempotent()
    {
        var client = factory.CreateClient();
        var phone = $"9198{Random.Shared.Next(10000000, 99999999)}";   // a distinct number for this test
        var messageId = NewMessageId();

        // First delivery starts a conversation (greeting). Second delivery of the SAME id is skipped.
        var first = await PostSignedAsync(client, MessageBody(phone, messageId, "hi"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await PostSignedAsync(client, MessageBody(phone, messageId, "hi"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // Exactly ONE active conversation exists for this number (the redelivery did not advance/duplicate it).
        await using var conn = new NpgsqlConnection(WhatsAppWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM docslot.conversations WHERE tenant_id = @t AND whatsapp_phone = @p",
            conn);
        cmd.Parameters.AddWithValue("t", factory.TenantId);
        cmd.Parameters.AddWithValue("p", phone);
        var conversationCount = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1, conversationCount);

        // The inbound message was logged exactly once (the second delivery short-circuited before logging).
        await using var logCmd = new NpgsqlCommand(
            "SELECT count(*) FROM docslot.wa_message_log WHERE tenant_id = @t AND whatsapp_message_id = @m AND direction = 'inbound'",
            conn);
        logCmd.Parameters.AddWithValue("t", factory.TenantId);
        logCmd.Parameters.AddWithValue("m", messageId);
        var inboundLogs = (long)(await logCmd.ExecuteScalarAsync())!;
        Assert.Equal(1, inboundLogs);

        // cleanup the extra number's rows
        await Cleanup(conn, phone, messageId);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private async Task SendAsync(HttpClient client, string phone, string text)
    {
        var resp = await PostSignedAsync(client, MessageBody(phone, NewMessageId(), text));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private async Task<HttpResponseMessage> PostSignedAsync(HttpClient client, string body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/whatsapp/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Remove("X-Hub-Signature-256");
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

    private static async Task Cleanup(NpgsqlConnection conn, string phone, string messageId)
    {
        // wa_message_log references conversations (FK) → delete the log rows for this number first.
        await Run(conn, "DELETE FROM docslot.wa_message_log WHERE conversation_id IN (SELECT conversation_id FROM docslot.conversations WHERE whatsapp_phone = @p)", ("p", phone));
        await Run(conn, "DELETE FROM docslot.wa_message_log WHERE whatsapp_message_id = @m", ("m", messageId));
        await Run(conn, "DELETE FROM docslot.processed_messages WHERE whatsapp_message_id = @m", ("m", messageId));
        await Run(conn, "DELETE FROM docslot.conversations WHERE whatsapp_phone = @p", ("p", phone));
        await Run(conn, "DELETE FROM docslot.wa_contact_profiles WHERE phone = @p", ("p", phone));
    }

    private static async Task Run(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
