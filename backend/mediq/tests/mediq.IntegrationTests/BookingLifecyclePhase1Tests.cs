using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.Application.Features.Docslot.Bookings;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-1 booking-lifecycle invariants against the live canonical DB via the Dashboard API:
/// <list type="bullet">
/// <item><b>Reschedule</b> terminates the old booking ('rescheduled'), mints a NEW booking on the new slot
/// linked via <c>rescheduled_from_booking_id</c>, frees the OLD slot (re-bookable) and consumes the new one;
/// a too-soon new slot (within cutoff) is 422; rescheduling a checked-in / terminal booking is 422.</item>
/// <item><b>Check-in</b>: confirmed → checked_in sets <c>checked_in_at</c>; checked_in → complete succeeds;
/// pending → check-in is an illegal transition (422).</item>
/// <item><b>Cutoff</b>: a slot within <c>bookingCutoffHours</c> (fixture default 2h) is rejected 422; a slot
/// beyond the cutoff succeeds.</item>
/// </list>
/// </summary>
public sealed class BookingLifecyclePhase1Tests(DocslotWebAppFactory factory) : IClassFixture<DocslotWebAppFactory>
{
    // ---- Reschedule -----------------------------------------------------------------------------------

    [Fact]
    public async Task Reschedule_Terminates_Old_Mints_New_Linked_FreesOldSlot_ConsumesNew()
    {
        var client = await AuthedClientAsync();
        var oldSlot = Guid.NewGuid();
        var newSlot = Guid.NewGuid();
        await SeedSlotAsync(oldSlot, new TimeOnly(9, 5));
        await SeedSlotAsync(newSlot, new TimeOnly(9, 20));

        var created = await CreateAsync(client, oldSlot);
        Assert.Equal((1, "booked"), await SlotStateAsync(oldSlot));   // old slot consumed at create

        var reschedule = await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/reschedule",
            Guid.NewGuid().ToString(), new { newSlotId = newSlot, reason = "patient asked for a later time" });
        Assert.Equal(HttpStatusCode.OK, reschedule.StatusCode);
        var result = await reschedule.Content.ReadFromJsonAsync<RescheduleBookingResult>();
        Assert.NotNull(result);
        Assert.Equal(created.BookingId, result!.OldBookingId);
        Assert.NotEqual(created.BookingId, result.NewBookingId);
        Assert.StartsWith("BKG-", result.NewBookingNumber!);

        // Old booking is terminal 'rescheduled'; new booking links back to it.
        Assert.Equal("rescheduled", await BookingStatusAsync(created.BookingId));
        var (newStatus, rescheduledFrom) = await NewBookingLineageAsync(result.NewBookingId);
        Assert.Equal("pending", newStatus);
        Assert.Equal(created.BookingId, rescheduledFrom);

        // Old slot is freed (re-bookable); new slot is consumed.
        Assert.Equal((0, "available"), await SlotStateAsync(oldSlot));
        Assert.Equal((1, "booked"), await SlotStateAsync(newSlot));
    }

    [Fact]
    public async Task Reschedule_To_TooSoon_Slot_Is_Rejected_422_And_Old_Untouched()
    {
        var client = await AuthedClientAsync();
        var oldSlot = Guid.NewGuid();
        await SeedSlotAsync(oldSlot, new TimeOnly(9, 35));
        var created = await CreateAsync(client, oldSlot);

        // A new slot TODAY within the 2h cutoff → reschedule's shared cutoff guard rejects it (422).
        var tooSoon = Guid.NewGuid();
        await SeedSlotAtAsync(tooSoon, DateOnly.FromDateTime(DateTime.UtcNow.Date), TimeOnly.FromDateTime(DateTime.UtcNow.AddMinutes(30)));

        var reschedule = await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/reschedule",
            Guid.NewGuid().ToString(), new { newSlotId = tooSoon });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reschedule.StatusCode);

        // The OLD booking is untouched (still pending) and its slot still consumed.
        Assert.Equal("pending", await BookingStatusAsync(created.BookingId));
        Assert.Equal((1, "booked"), await SlotStateAsync(oldSlot));
        Assert.Equal((0, "available"), await SlotStateAsync(tooSoon));   // too-soon slot never consumed
    }

    [Fact]
    public async Task Reschedule_CheckedIn_Booking_Is_Rejected_422()
    {
        var client = await AuthedClientAsync();
        var slot = Guid.NewGuid();
        var newSlot = Guid.NewGuid();
        await SeedSlotAsync(slot, new TimeOnly(9, 50));
        await SeedSlotAsync(newSlot, new TimeOnly(10, 5));
        var created = await CreateAsync(client, slot);

        // Drive to checked_in: approve → check-in.
        await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/approve", Guid.NewGuid().ToString(), new { });
        var checkIn = await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/check-in", Guid.NewGuid().ToString(), new { });
        Assert.Equal(HttpStatusCode.OK, checkIn.StatusCode);

        var reschedule = await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/reschedule",
            Guid.NewGuid().ToString(), new { newSlotId = newSlot });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reschedule.StatusCode);
        Assert.Equal("checked_in", await BookingStatusAsync(created.BookingId));
    }

    [Fact]
    public async Task Reschedule_Terminal_Booking_Is_Rejected_422()
    {
        var client = await AuthedClientAsync();
        var slot = Guid.NewGuid();
        var newSlot = Guid.NewGuid();
        await SeedSlotAsync(slot, new TimeOnly(10, 20));
        await SeedSlotAsync(newSlot, new TimeOnly(10, 35));
        var created = await CreateAsync(client, slot);

        // Cancel → terminal.
        var cancel = await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/cancel",
            Guid.NewGuid().ToString(), new { reason = "patient cancelled" });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        var reschedule = await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/reschedule",
            Guid.NewGuid().ToString(), new { newSlotId = newSlot });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reschedule.StatusCode);
        Assert.Equal("cancelled", await BookingStatusAsync(created.BookingId));
    }

    // ---- Check-in -------------------------------------------------------------------------------------

    [Fact]
    public async Task CheckIn_Confirmed_To_CheckedIn_Then_Complete()
    {
        var client = await AuthedClientAsync();
        var slot = Guid.NewGuid();
        await SeedSlotAsync(slot, new TimeOnly(10, 50));
        var created = await CreateAsync(client, slot);

        await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/approve", Guid.NewGuid().ToString(), new { });

        var checkIn = await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/check-in", Guid.NewGuid().ToString(), new { });
        Assert.Equal(HttpStatusCode.OK, checkIn.StatusCode);
        var result = await checkIn.Content.ReadFromJsonAsync<BookingActionResultDto>();
        Assert.Equal(mediq.SharedDataModel.Docslot.Dashboard.Enums.BookingStatus.CheckedIn, result!.NewStatus);

        // checked_in_at is stamped.
        Assert.NotNull(await CheckedInAtAsync(created.BookingId));

        // checked_in → complete succeeds.
        var complete = await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/complete", Guid.NewGuid().ToString(), new { });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal("completed", await BookingStatusAsync(created.BookingId));
    }

    [Fact]
    public async Task CheckIn_From_Pending_Is_Rejected_422()
    {
        var client = await AuthedClientAsync();
        var slot = Guid.NewGuid();
        await SeedSlotAsync(slot, new TimeOnly(11, 5));
        var created = await CreateAsync(client, slot);   // stays pending (no approve)

        var checkIn = await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/check-in", Guid.NewGuid().ToString(), new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, checkIn.StatusCode);
        Assert.Equal("pending", await BookingStatusAsync(created.BookingId));
        Assert.Null(await CheckedInAtAsync(created.BookingId));
    }

    // ---- Cutoff ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_On_Slot_Within_Cutoff_Is_Rejected_422()
    {
        var client = await AuthedClientAsync();
        var slot = Guid.NewGuid();
        // Today, 30 min out → inside the fixture's 2h bookingCutoffHours → rejected.
        await SeedSlotAtAsync(slot, DateOnly.FromDateTime(DateTime.UtcNow.Date), TimeOnly.FromDateTime(DateTime.UtcNow.AddMinutes(30)));

        var key = Guid.NewGuid().ToString();
        var resp = await PostAsync(client, "/api/v1/bookings", key, new CreateBookingRequest(
            slot, factory.DoctorId, factory.DepartmentId, factory.PatientPhone,
            "Cutoff Test", 30, "male", "consultation", "dashboard", null, false, key));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal((0, "available"), await SlotStateAsync(slot));   // not consumed
    }

    [Fact]
    public async Task Create_On_Slot_Beyond_Cutoff_Succeeds()
    {
        var client = await AuthedClientAsync();
        var slot = Guid.NewGuid();
        // 3 days out (factory.SlotDate is today+3) → well beyond the 2h cutoff → succeeds.
        await SeedSlotAsync(slot, new TimeOnly(11, 20));

        var created = await CreateAsync(client, slot);
        Assert.StartsWith("BKG-", created.BookingNumber!);
        Assert.Equal((1, "booked"), await SlotStateAsync(slot));
    }

    // ---- helpers --------------------------------------------------------------------------------------

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

    private async Task<CreateBookingResult> CreateAsync(HttpClient client, Guid slotId)
    {
        var key = Guid.NewGuid().ToString();
        var resp = await PostAsync(client, "/api/v1/bookings", key, new CreateBookingRequest(
            slotId, factory.DoctorId, factory.DepartmentId, factory.PatientPhone,
            "Lifecycle Test", 30, "male", "consultation", "dashboard", "Fever", IssueOpdToken: false, key));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<CreateBookingResult>();
        Assert.NotNull(created);
        return created!;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string idempotencyKey, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(req);
    }

    private Task SeedSlotAsync(Guid slotId, TimeOnly start) =>
        SeedSlotAtAsync(slotId, DocslotWebAppFactory.SlotDate, start);

    private async Task SeedSlotAtAsync(Guid slotId, DateOnly date, TimeOnly start)
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
        cmd.Parameters.AddWithValue("date", date);
        cmd.Parameters.AddWithValue("start", start);
        cmd.Parameters.AddWithValue("end", start.AddMinutes(15));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<(int Count, string Status)> SlotStateAsync(Guid slotId)
    {
        await using var conn = new NpgsqlConnection(DocslotWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT current_count, status FROM docslot.time_slots WHERE slot_id=@id", conn);
        cmd.Parameters.AddWithValue("id", slotId);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        return (rd.GetInt16(0), rd.GetString(1));
    }

    private static async Task<string> BookingStatusAsync(Guid bookingId)
    {
        await using var conn = new NpgsqlConnection(DocslotWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT status FROM docslot.bookings WHERE booking_id=@b", conn);
        cmd.Parameters.AddWithValue("b", bookingId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<(string Status, Guid? RescheduledFrom)> NewBookingLineageAsync(Guid bookingId)
    {
        await using var conn = new NpgsqlConnection(DocslotWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT status, rescheduled_from_booking_id FROM docslot.bookings WHERE booking_id=@b", conn);
        cmd.Parameters.AddWithValue("b", bookingId);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        return (rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetGuid(1));
    }

    private static async Task<DateTime?> CheckedInAtAsync(Guid bookingId)
    {
        await using var conn = new NpgsqlConnection(DocslotWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT checked_in_at FROM docslot.bookings WHERE booking_id=@b", conn);
        cmd.Parameters.AddWithValue("b", bookingId);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? null : (DateTime)result;
    }
}
