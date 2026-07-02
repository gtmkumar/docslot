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
/// a past new slot is 422 (old untouched); a within-cutoff FUTURE slot succeeds (staff-driven); rescheduling
/// a checked-in / terminal booking is 422.</item>
/// <item><b>Check-in</b>: confirmed → checked_in sets <c>checked_in_at</c> and the LIST read-model surfaces
/// CheckedIn (not Pending); checked_in → complete succeeds; pending → check-in is illegal (422).</item>
/// <item><b>Slot timing</b>: the <c>bookingCutoffHours</c> lead-time (fixture default 2h) rejects only
/// SELF-SERVICE channels (whatsapp/api); staff channels (dashboard/walk_in/phone_call) may book any FUTURE
/// slot; an already-started slot is 422 on every channel.</item>
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
    public async Task Reschedule_To_Past_Slot_Is_Rejected_422_And_Old_Untouched()
    {
        var client = await AuthedClientAsync();
        var oldSlot = Guid.NewGuid();
        await SeedSlotAsync(oldSlot, new TimeOnly(9, 35));
        var created = await CreateAsync(client, oldSlot);

        // A slot that already started (1h ago IST) → the shared slot-timing guard rejects it (422).
        var past = Guid.NewGuid();
        SeedIstSlotAt(out var pastDate, out var pastTime, minutesFromNow: -60);
        await SeedSlotAtAsync(past, pastDate, pastTime);

        var reschedule = await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/reschedule",
            Guid.NewGuid().ToString(), new { newSlotId = past });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reschedule.StatusCode);

        // The OLD booking is untouched (still pending) and its slot still consumed.
        Assert.Equal("pending", await BookingStatusAsync(created.BookingId));
        Assert.Equal((1, "booked"), await SlotStateAsync(oldSlot));
        Assert.Equal((0, "available"), await SlotStateAsync(past));   // past slot never consumed
    }

    [Fact]
    public async Task Reschedule_To_Future_Slot_Within_Cutoff_Succeeds_StaffDriven()
    {
        var client = await AuthedClientAsync();
        var oldSlot = Guid.NewGuid();
        await SeedSlotAsync(oldSlot, new TimeOnly(9, 40));
        var created = await CreateAsync(client, oldSlot);

        // 30 min out (inside the 2h cutoff) — reschedules are staff-driven, so the cutoff does NOT apply;
        // only already-started slots are refused.
        var soon = Guid.NewGuid();
        SeedIstSlotAt(out var soonDate, out var soonTime, minutesFromNow: 30);
        await SeedSlotAtAsync(soon, soonDate, soonTime);

        var reschedule = await PostAsync(client, $"/api/v1/bookings/{created.BookingId}/reschedule",
            Guid.NewGuid().ToString(), new { newSlotId = soon, reason = "walk-in moved up" });
        Assert.Equal(HttpStatusCode.OK, reschedule.StatusCode);
        Assert.Equal("rescheduled", await BookingStatusAsync(created.BookingId));
        Assert.Equal((1, "booked"), await SlotStateAsync(soon));
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

        // Regression (list read-model): the LIST endpoint must surface checked_in as CheckedIn — a missing
        // EnumParse arm once defaulted it to Pending, putting checked-in patients back in the approval queue.
        var list = await client.GetFromJsonAsync<List<BookingListItemDto>>("/api/v1/bookings");
        var row = list!.Single(b => b.BookingId == created.BookingId);
        Assert.Equal(mediq.SharedDataModel.Docslot.Dashboard.Enums.BookingStatus.CheckedIn, row.Status);

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

    // ---- Slot-timing guard (past slots + channel-aware cutoff) -----------------------------------------
    //
    // The cutoff shields the clinic from last-minute SELF-SERVICE bookings (whatsapp/api). Staff channels
    // (dashboard/walk_in/phone_call) bypass it — the front desk must be able to register the walk-in
    // standing at the desk — but NO channel may book a slot that has already started.

    [Fact]
    public async Task Create_SelfService_Within_Cutoff_Is_Rejected_422()
    {
        var client = await AuthedClientAsync();
        var slot = Guid.NewGuid();
        // 30 min out IST → inside the fixture's 2h bookingCutoffHours → rejected for a whatsapp booking.
        SeedIstSlotAt(out var date, out var time, minutesFromNow: 30);
        await SeedSlotAtAsync(slot, date, time);

        var key = Guid.NewGuid().ToString();
        var resp = await PostAsync(client, "/api/v1/bookings", key, new CreateBookingRequest(
            slot, factory.DoctorId, factory.DepartmentId, factory.PatientPhone,
            "Cutoff Test", 30, "male", "consultation", "whatsapp", null, false, key));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal((0, "available"), await SlotStateAsync(slot));   // not consumed
    }

    [Fact]
    public async Task Create_Staff_Walkin_Within_Cutoff_Succeeds()
    {
        var client = await AuthedClientAsync();
        var slot = Guid.NewGuid();
        // 30 min out IST — inside the cutoff, but walk_in is a staff channel → allowed.
        SeedIstSlotAt(out var date, out var time, minutesFromNow: 30);
        await SeedSlotAtAsync(slot, date, time);

        var key = Guid.NewGuid().ToString();
        var resp = await PostAsync(client, "/api/v1/bookings", key, new CreateBookingRequest(
            slot, factory.DoctorId, factory.DepartmentId, factory.PatientPhone,
            "Walk-in Test", 30, "male", "consultation", "walk_in", null, false, key));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal((1, "booked"), await SlotStateAsync(slot));
    }

    [Fact]
    public async Task Create_On_Past_Slot_Is_Rejected_422_Even_For_Staff()
    {
        var client = await AuthedClientAsync();
        var slot = Guid.NewGuid();
        // Started 1h ago IST → rejected on every channel, staff included.
        SeedIstSlotAt(out var date, out var time, minutesFromNow: -60);
        await SeedSlotAtAsync(slot, date, time);

        var key = Guid.NewGuid().ToString();
        var resp = await PostAsync(client, "/api/v1/bookings", key, new CreateBookingRequest(
            slot, factory.DoctorId, factory.DepartmentId, factory.PatientPhone,
            "Past Slot Test", 30, "male", "consultation", "dashboard", null, false, key));
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

    /// <summary>
    /// slot_date/start_time are IST WALL CLOCK (converted via AT TIME ZONE 'Asia/Kolkata' when compared to
    /// now) — so relative-to-now seeds must be computed in IST, not UTC, or "30 min out" lands hours off.
    /// </summary>
    private static void SeedIstSlotAt(out DateOnly date, out TimeOnly time, int minutesFromNow)
    {
        var istWallClock = DateTime.UtcNow.AddMinutes(minutesFromNow).AddMinutes(330);   // UTC+05:30
        date = DateOnly.FromDateTime(istWallClock);
        time = TimeOnly.FromDateTime(istWallClock);
    }

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
