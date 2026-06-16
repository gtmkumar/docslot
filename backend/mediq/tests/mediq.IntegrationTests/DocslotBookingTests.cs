using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.Application.Abstractions;
using mediq.Infrastructure.Idempotency;
using mediq.Infrastructure.Persistence;
using mediq.Application.Features.Docslot.Bookings;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice-03 booking-core invariants against the live canonical DB: create→hold(5min)→approve→complete
/// happy path; illegal transition rejected; idempotent approve doesn't double-confirm AND the durable
/// store de-dups across a NEW store instance (simulated restart); dashboard summary; integration-event emit.
/// </summary>
public sealed class DocslotBookingTests(DocslotWebAppFactory factory) : IClassFixture<DocslotWebAppFactory>
{
    [Fact]
    public async Task Create_Hold_Approve_Complete_HappyPath_And_Emits_Events()
    {
        var client = await AuthedClientAsync();

        // Create (takes a 5-min slot hold). Idempotency-Key required.
        var createKey = Guid.NewGuid().ToString();
        var createResp = await PostAsync(client, "/api/v1/bookings", createKey, new CreateBookingRequest(
            factory.SlotId, factory.DoctorId, factory.DepartmentId, factory.PatientPhone,
            "Test Patient", 35, "male", "consultation", "dashboard", "Fever", IssueOpdToken: true, createKey));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<CreateBookingResult>();
        Assert.NotNull(created);
        Assert.NotNull(created!.BookingNumber);                 // assigned by DB trigger
        Assert.StartsWith("BKG-", created.BookingNumber!);
        Assert.NotNull(created.TokenNumber);                    // OPD token issued

        // The slot is now held/consumed → a second create on the same slot must fail (slot unavailable).
        var dupResp = await PostAsync(client, "/api/v1/bookings", Guid.NewGuid().ToString(), new CreateBookingRequest(
            factory.SlotId, factory.DoctorId, factory.DepartmentId, factory.PatientPhone,
            "Test Patient", 35, "male", "consultation", "dashboard", null, false, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dupResp.StatusCode);

        // Approve → confirmed.
        var approve = await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/approve", Guid.NewGuid().ToString(), new { });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var approved = await approve.Content.ReadFromJsonAsync<BookingActionResultDto>();
        Assert.Equal(mediq.SharedDataModel.Docslot.Dashboard.Enums.BookingStatus.Confirmed, approved!.NewStatus);

        // Complete → completed.
        var complete = await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/complete", Guid.NewGuid().ToString(), new { });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var completed = await complete.Content.ReadFromJsonAsync<BookingActionResultDto>();
        Assert.Equal(mediq.SharedDataModel.Docslot.Dashboard.Enums.BookingStatus.Completed, completed!.NewStatus);

        // Integration events emitted for created + approved + completed.
        var types = DocslotWebAppFactory.Publisher.Published.Select(e => e.EventType).ToList();
        Assert.Contains(BookingEventTypes.Created, types);
        Assert.Contains(BookingEventTypes.Confirmed, types);   // docslot.booking.approved
        Assert.Contains(BookingEventTypes.Completed, types);
    }

    [Fact]
    public async Task Illegal_Transition_Is_Rejected()
    {
        var client = await AuthedClientAsync();

        // Create a pending booking on the spare slot.
        var key = Guid.NewGuid().ToString();
        var createResp = await PostAsync(client, "/api/v1/bookings", key, new CreateBookingRequest(
            factory.SecondSlotId, factory.DoctorId, factory.DepartmentId, factory.PatientPhone,
            "Test Patient", 35, "male", "consultation", "dashboard", null, false, key));
        var created = await createResp.Content.ReadFromJsonAsync<CreateBookingResult>();

        // Completing a PENDING booking is illegal (must be confirmed first) → 422.
        var complete = await PostAsync(client, $"/api/v1/bookings/{created!.BookingId}/complete", Guid.NewGuid().ToString(), new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, complete.StatusCode);
    }

    [Fact]
    public async Task Idempotent_Approve_Does_Not_Double_Confirm_And_DeDups_Across_Restart()
    {
        var client = await AuthedClientAsync();

        // Create on a fresh slot via SQL-independent path: reuse the happy-path slot is consumed, so make one.
        var slotId = Guid.NewGuid();
        await SeedSpareSlotAsync(slotId);

        var createKey = Guid.NewGuid().ToString();
        var createResp = await PostAsync(client, "/api/v1/bookings", createKey, new CreateBookingRequest(
            slotId, factory.DoctorId, factory.DepartmentId, factory.PatientPhone,
            "Test Patient", 35, "male", "consultation", "dashboard", null, false, createKey));
        var created = await createResp.Content.ReadFromJsonAsync<CreateBookingResult>();

        var approveKey = Guid.NewGuid().ToString();
        var first = await PostAsync(client, $"/api/v1/bookings/{created!.BookingId}/approve", approveKey, new { });
        var firstResult = await first.Content.ReadFromJsonAsync<BookingActionResultDto>();
        Assert.False(firstResult!.WasReplayed);

        // Replay the SAME idempotency key → served from the DURABLE store; WasReplayed=true; no double-confirm.
        var replay = await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/approve", approveKey, new { });
        var replayResult = await replay.Content.ReadFromJsonAsync<BookingActionResultDto>();
        Assert.True(replayResult!.WasReplayed);
        Assert.Equal(firstResult.NewStatus, replayResult.NewStatus);

        // Simulate a restart/scale-out: a BRAND-NEW store instance against the same DB still de-dups.
        using var scope = factory.Services.CreateScope();
        var freshStore = ActivatorUtilities.CreateInstance<DurableIdempotencyStore>(
            scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<PlatformDbContext>());
        var endpoint = $"POST /api/v1/bookings/{created.BookingId}/approve";
        var cached = await freshStore.TryGetAsync(factory.TenantId, endpoint, approveKey, default);
        Assert.NotNull(cached);   // durable record survives a new store instance

        // Exactly one status-history 'confirmed' row exists (no double-confirm).
        var confirmCount = await CountStatusHistoryAsync(created.BookingId, "confirmed");
        Assert.Equal(1, confirmCount);
    }

    [Fact]
    public async Task Dashboard_Summary_Reflects_Seeded_Bookings()
    {
        var client = await AuthedClientAsync();

        // Create + approve one booking so confirmed-today is at least 1.
        var slotId = Guid.NewGuid();
        await SeedSpareSlotAsync(slotId);
        var key = Guid.NewGuid().ToString();
        var createResp = await PostAsync(client, "/api/v1/bookings", key, new CreateBookingRequest(
            slotId, factory.DoctorId, factory.DepartmentId, factory.PatientPhone,
            "Test Patient", 35, "male", "consultation", "dashboard", null, false, key));
        var created = await createResp.Content.ReadFromJsonAsync<CreateBookingResult>();
        await PostAsync(client, $"/api/v1/bookings/{created!.BookingId}/approve", Guid.NewGuid().ToString(), new { });

        var summaryResp = await client.GetAsync("/api/v1/dashboard/summary");
        Assert.Equal(HttpStatusCode.OK, summaryResp.StatusCode);
        var summary = await summaryResp.Content.ReadFromJsonAsync<DashboardSummaryDto>();
        Assert.NotNull(summary);
        Assert.Equal("INR", summary!.RevenueCurrency);
        Assert.True(summary.ConfirmedTodayCount >= 1);
        Assert.True(summary.TodayRevenue >= 500m);              // the doctor's consultation fee
    }

    [Fact]
    public async Task Booking_List_Masks_Phone()
    {
        var client = await AuthedClientAsync();
        var slotId = Guid.NewGuid();
        await SeedSpareSlotAsync(slotId);
        var key = Guid.NewGuid().ToString();
        await PostAsync(client, "/api/v1/bookings", key, new CreateBookingRequest(
            slotId, factory.DoctorId, factory.DepartmentId, factory.PatientPhone,
            "Test Patient", 35, "male", "consultation", "dashboard", null, false, key));

        var listResp = await client.GetAsync("/api/v1/bookings?take=50");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var items = await listResp.Content.ReadFromJsonAsync<List<BookingListItemDto>>();
        Assert.NotEmpty(items!);
        // No row exposes the raw phone; masking inserts 'x' and drops the middle digits.
        Assert.All(items!, i => Assert.DoesNotContain(factory.PatientPhone, i.MaskedPhone));
        Assert.Contains(items!, i => i.MaskedPhone.Contains('x'));
    }

    // ---- helpers -------------------------------------------------------------------------------

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

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string idempotencyKey, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }

    private async Task SeedSpareSlotAsync(Guid slotId)
    {
        await using var conn = new Npgsql.NpgsqlConnection(DocslotWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            """
            INSERT INTO docslot.time_slots (slot_id, tenant_id, doctor_id, slot_date, start_time, end_time, status, current_count, max_count, created_at)
            VALUES (@id, @tid, @doc, @date, @start, @end, 'available', 0, 1, NOW())
            """, conn);
        var start = new TimeOnly(11, 0).AddMinutes(Random.Shared.Next(0, 600));
        cmd.Parameters.AddWithValue("id", slotId);
        cmd.Parameters.AddWithValue("tid", factory.TenantId);
        cmd.Parameters.AddWithValue("doc", factory.DoctorId);
        cmd.Parameters.AddWithValue("date", DocslotWebAppFactory.SlotDate);
        cmd.Parameters.AddWithValue("start", start);
        cmd.Parameters.AddWithValue("end", start.AddMinutes(15));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> CountStatusHistoryAsync(Guid bookingId, string toStatus)
    {
        await using var conn = new Npgsql.NpgsqlConnection(DocslotWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT COUNT(*)::int FROM docslot.booking_status_history WHERE booking_id = @b AND to_status = @s", conn);
        cmd.Parameters.AddWithValue("b", bookingId);
        cmd.Parameters.AddWithValue("s", toStatus);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
