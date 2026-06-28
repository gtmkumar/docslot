using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.Application.Abstractions;
using mediq.Application.Features.Docslot.Bookings;
using mediq.Application.Features.Docslot.Doctors;
using mediq.SharedDataModel.Docslot.Auth;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-0 booking data-plane invariants against the live canonical DB:
/// (1) the slot generator materializes bookable inventory from a doctor's weekly schedule via the live
///     endpoint; (2) cancelling a confirmed booking FREES the slot capacity it consumed (re-bookable);
/// (3) the (slot,doctor) consistency guard rejects a slot id paired with an unrelated doctor.
/// </summary>
public sealed class SlotManagementTests(DocslotWebAppFactory factory) : IClassFixture<DocslotWebAppFactory>
{
    [Fact]
    public async Task GenerateSlots_FromSchedule_CreatesBookableInventory()
    {
        var client = await AuthedClientAsync();
        // Seed a weekly schedule for the SlotDate's weekday: 14:00–16:00, 30-min slots, no break → 4 slots.
        await SeedScheduleAsync((short)(int)DocslotWebAppFactory.SlotDate.DayOfWeek,
            new TimeOnly(14, 0), new TimeOnly(16, 0), durationMin: 30);

        var resp = await client.PostAsync(
            $"/api/v1/doctors/{factory.DoctorId}/slots/generate?from={DocslotWebAppFactory.SlotDate:yyyy-MM-dd}&to={DocslotWebAppFactory.SlotDate:yyyy-MM-dd}",
            content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<GenerateSlotsResult>();
        Assert.NotNull(result);
        Assert.Equal(4, result!.SlotsCreated);

        // Re-running is idempotent (no new rows).
        var again = await client.PostAsync(
            $"/api/v1/doctors/{factory.DoctorId}/slots/generate?from={DocslotWebAppFactory.SlotDate:yyyy-MM-dd}&to={DocslotWebAppFactory.SlotDate:yyyy-MM-dd}",
            content: null);
        var againResult = await again.Content.ReadFromJsonAsync<GenerateSlotsResult>();
        Assert.Equal(0, againResult!.SlotsCreated);

        // The generated 14:00 slot is now bookable via the slots read.
        var slots = await client.GetFromJsonAsync<List<SlotDto>>(
            $"/api/v1/doctors/{factory.DoctorId}/slots?date={DocslotWebAppFactory.SlotDate:yyyy-MM-dd}");
        Assert.Contains(slots!, s => s.StartTime.Hour == 14 && s.StartTime.Minute == 0);
    }

    [Fact]
    public async Task CancelBooking_FreesSlotCapacity()
    {
        var client = await AuthedClientAsync();
        var slotId = Guid.NewGuid();
        await SeedSlotAsync(slotId, new TimeOnly(17, 0));   // fresh single-capacity slot

        // Book it → capacity consumed (current_count 1, status 'booked').
        var key = Guid.NewGuid().ToString();
        var createResp = await PostAsync(client, "/api/v1/bookings", key, new CreateBookingRequest(
            slotId, factory.DoctorId, factory.DepartmentId, factory.PatientPhone,
            "Cap Test", 30, "male", "consultation", "dashboard", "Fever", IssueOpdToken: false, key));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<CreateBookingResult>();
        Assert.Equal((1, "booked"), await SlotStateAsync(slotId));

        // Cancel → capacity freed: current_count back to 0, status back to 'available'.
        var cancel = await PostAsync(client, $"/api/v1/bookings/{created!.BookingId}/cancel",
            Guid.NewGuid().ToString(), new { reason = "patient request" });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal((0, "available"), await SlotStateAsync(slotId));
    }

    [Fact]
    public async Task CreateBooking_WithMismatchedDoctor_IsRejected()
    {
        var client = await AuthedClientAsync();
        var slotId = Guid.NewGuid();
        await SeedSlotAsync(slotId, new TimeOnly(18, 0));

        // Valid slot id, but an unrelated doctor id → the (slot,doctor) guard fails the hold → 422.
        var key = Guid.NewGuid().ToString();
        var resp = await PostAsync(client, "/api/v1/bookings", key, new CreateBookingRequest(
            slotId, Guid.NewGuid(), factory.DepartmentId, factory.PatientPhone,
            "Mismatch", 30, "male", "consultation", "dashboard", "Fever", IssueOpdToken: false, key));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        // The slot was NOT consumed.
        Assert.Equal((0, "available"), await SlotStateAsync(slotId));
    }

    // ---- helpers ------------------------------------------------------------------------------------

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

    private async Task SeedScheduleAsync(short dayOfWeek, TimeOnly start, TimeOnly end, int durationMin)
    {
        await using var conn = new NpgsqlConnection(DocslotWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO docslot.doctor_schedules
                (doctor_id, day_of_week, start_time, end_time, slot_duration_minutes, max_patients_per_slot, is_active)
            VALUES (@doc, @dow, @start, @end, @dur, 1, true)
            """, conn);
        cmd.Parameters.AddWithValue("doc", factory.DoctorId);
        cmd.Parameters.AddWithValue("dow", dayOfWeek);
        cmd.Parameters.AddWithValue("start", start);
        cmd.Parameters.AddWithValue("end", end);
        cmd.Parameters.AddWithValue("dur", (short)durationMin);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedSlotAsync(Guid slotId, TimeOnly start)
    {
        await using var conn = new NpgsqlConnection(DocslotWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO docslot.time_slots (slot_id, tenant_id, doctor_id, slot_date, start_time, end_time, status, current_count, max_count, created_at)
            VALUES (@id, @tid, @doc, @date, @start, @end, 'available', 0, 1, NOW())
            """, conn);
        cmd.Parameters.AddWithValue("id", slotId);
        cmd.Parameters.AddWithValue("tid", factory.TenantId);
        cmd.Parameters.AddWithValue("doc", factory.DoctorId);
        cmd.Parameters.AddWithValue("date", DocslotWebAppFactory.SlotDate);
        cmd.Parameters.AddWithValue("start", start);
        cmd.Parameters.AddWithValue("end", start.AddMinutes(15));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<(int Count, string Status)> SlotStateAsync(Guid slotId)
    {
        await using var conn = new NpgsqlConnection(DocslotWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT current_count, status FROM docslot.time_slots WHERE slot_id = @id", conn);
        cmd.Parameters.AddWithValue("id", slotId);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        return (rd.GetInt16(0), rd.GetString(1));
    }
}
