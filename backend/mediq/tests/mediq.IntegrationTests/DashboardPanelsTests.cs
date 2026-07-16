using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// The dashboard's three side-panel read endpoints (agent-panel / department-load / floor) against the
/// live canonical DB. Panel data is seeded DIRECTLY via SQL (today-IST slots, completed/whatsapp
/// bookings, wa_message_log rows) because the panels aggregate "today in Asia/Kolkata" while the shared
/// factory seeds slots 3 days out to clear the booking cutoff. Assertions are lower-bound (&gt;=) where
/// sibling tests in this class can add to the same tenant aggregates.
/// </summary>
public sealed class DashboardPanelsTests(DocslotWebAppFactory factory) : IClassFixture<DocslotWebAppFactory>
{
    /// <summary>Today in IST — the date the panels aggregate on (NOT the factory's cutoff-clearing SlotDate).</summary>
    private static DateOnly TodayIst => DateOnly.FromDateTime(DateTime.UtcNow.Add(TimeSpan.FromMinutes(330)));

    [Fact]
    public async Task DepartmentLoad_Aggregates_Todays_Slots_Per_Department()
    {
        var client = await AuthedClientAsync();

        // A today-IST slot with 1/3 occupancy on the seeded doctor (General Medicine).
        await SeedTodaySlotAsync(Guid.NewGuid(), new TimeOnly(9, 0), currentCount: 1, maxCount: 3);

        var resp = await client.GetAsync("/api/v1/dashboard/department-load");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var rows = await resp.Content.ReadFromJsonAsync<List<DepartmentLoadDto>>();
        Assert.NotNull(rows);

        var dept = Assert.Single(rows!, r => r.DepartmentId == factory.DepartmentId);
        Assert.Equal("General Medicine", dept.Name);
        Assert.True(dept.Booked >= 1);
        Assert.True(dept.Capacity >= 3);
        Assert.True(dept.Capacity >= dept.Booked);
        // ColorKey is a design TOKEN key, never a hex value.
        Assert.Matches("^[a-z]+$", dept.ColorKey);
    }

    [Fact]
    public async Task Floor_Lists_Doctor_With_Opd_Today_And_Counts_Seen()
    {
        var client = await AuthedClientAsync();

        // A consumed today-slot carrying a COMPLETED booking → the doctor is "on the floor", seen >= 1.
        var slotId = Guid.NewGuid();
        await SeedTodaySlotAsync(slotId, new TimeOnly(9, 30), currentCount: 1, maxCount: 1);
        await ExecAsync(
            """
            INSERT INTO docslot.bookings (booking_id, tenant_id, slot_id, patient_id, doctor_id, department_id,
                                          status, booked_via, booked_for, direct_discount_inr, booked_at, updated_at)
            VALUES (@id, @t, @s, @p, @d, @dep, 'completed', 'dashboard', 'self', 0, NOW(), NOW())
            """,
            ("id", Guid.NewGuid()), ("t", factory.TenantId), ("s", slotId),
            ("p", factory.PatientId), ("d", factory.DoctorId), ("dep", factory.DepartmentId));

        var resp = await client.GetAsync("/api/v1/dashboard/floor");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var rows = await resp.Content.ReadFromJsonAsync<List<FloorDoctorDto>>();
        Assert.NotNull(rows);

        var doc = Assert.Single(rows!, r => r.DoctorId == factory.DoctorId);
        Assert.Equal("Dr Slice03", doc.Name);
        Assert.Equal("General Medicine", doc.DepartmentName);
        Assert.True(doc.SeenToday >= 1);
        // NextSlot is nullable by contract (no free slot left today) — only shape-checked here because the
        // seeded 09:30 slot is in the past for most run times.
    }

    [Fact]
    public async Task AgentPanel_Derives_Funnel_And_Conversation_Metrics()
    {
        var client = await AuthedClientAsync();

        // One patient's full whatsapp journey today: dept + slot + confirmed → every funnel stage counts them.
        var slotId = Guid.NewGuid();
        await SeedTodaySlotAsync(slotId, new TimeOnly(10, 30), currentCount: 1, maxCount: 1);
        await ExecAsync(
            """
            INSERT INTO docslot.bookings (booking_id, tenant_id, slot_id, patient_id, doctor_id, department_id,
                                          status, booked_via, booked_for, direct_discount_inr, booked_at, updated_at)
            VALUES (@id, @t, @s, @p, @d, @dep, 'confirmed', 'whatsapp', 'self', 0, NOW(), NOW())
            """,
            ("id", Guid.NewGuid()), ("t", factory.TenantId), ("s", slotId),
            ("p", factory.PatientId), ("d", factory.DoctorId), ("dep", factory.DepartmentId));

        // An inbound→outbound message pair 10 minutes ago (2-minute gap) → activeConversations >= 1,
        // avgResponse ~2 min, at least one non-zero sparkline bucket.
        var inboundId = Guid.NewGuid();
        var outboundId = Guid.NewGuid();
        try
        {
            await ExecAsync(
                """
                INSERT INTO docslot.wa_message_log (log_id, tenant_id, patient_id, direction, message_type, content, status, sent_at)
                VALUES (@in,  @t, @p, 'inbound',  'text', '{"text":"hi"}'::jsonb,   'received', NOW() - INTERVAL '10 minutes'),
                       (@out, @t, @p, 'outbound', 'text', '{"text":"hello"}'::jsonb, 'sent',     NOW() - INTERVAL '8 minutes')
                """,
                ("in", inboundId), ("out", outboundId), ("t", factory.TenantId), ("p", factory.PatientId));

            var resp = await client.GetAsync("/api/v1/dashboard/agent-panel");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var panel = await resp.Content.ReadFromJsonAsync<AgentPanelDto>();
            Assert.NotNull(panel);

            Assert.True(panel!.ActiveConversations >= 1);
            Assert.True(panel.AvgResponseMins > 0m);
            Assert.Equal(24, panel.Sparkline.Count);
            Assert.Contains(panel.Sparkline, v => v > 0m);
            Assert.All(panel.Sparkline, v => Assert.InRange(v, 0m, 1m));

            // Funnel: 4 contract-keyed stages, monotonic non-increasing, pct against the greeted basis.
            Assert.Equal(new[] { "greeted", "selectedDept", "pickedSlot", "confirmed" },
                panel.Funnel.Select(s => s.Key).ToArray());
            Assert.True(panel.Funnel[0].Count >= 1);
            for (var i = 1; i < panel.Funnel.Count; i++)
                Assert.True(panel.Funnel[i].Count <= panel.Funnel[i - 1].Count);
            Assert.Equal(100m, panel.Funnel[0].Pct);

            // The seeded patient's journey is confirmed via whatsapp → self-served is a real percentage.
            Assert.InRange(panel.SelfServedPct, 0m, 100m);
            Assert.InRange(panel.HandedPct, 0m, 100m);
            Assert.InRange(panel.DropOffPct, 0m, 100m);
        }
        finally
        {
            // The shared factory's DisposeAsync doesn't clean wa_message_log; leaving rows would break its
            // patient delete (FK). Remove them here regardless of assertion outcome.
            await ExecAsync("DELETE FROM docslot.wa_message_log WHERE log_id IN (@a, @b)",
                ("a", inboundId), ("b", outboundId));
        }
    }

    [Fact]
    public async Task Panels_Require_Authentication()
    {
        var anon = factory.CreateClient();
        foreach (var url in new[]
                 {
                     "/api/v1/dashboard/agent-panel",
                     "/api/v1/dashboard/department-load",
                     "/api/v1/dashboard/floor",
                 })
        {
            var resp = await anon.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
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

    /// <summary>Seeds a slot on TODAY (IST) — the panels aggregate today's slots, unlike the factory's
    /// 3-days-out SlotDate. Direct SQL, so the booking cutoff doesn't apply.</summary>
    private async Task SeedTodaySlotAsync(Guid slotId, TimeOnly start, short currentCount, short maxCount)
    {
        await ExecAsync(
            """
            INSERT INTO docslot.time_slots (slot_id, tenant_id, doctor_id, slot_date, start_time, end_time, status, current_count, max_count, created_at)
            VALUES (@id, @tid, @doc, @date, @start, @end, 'available', @cur, @max, NOW())
            ON CONFLICT (doctor_id, slot_date, start_time) DO NOTHING
            """,
            ("id", slotId), ("tid", factory.TenantId), ("doc", factory.DoctorId), ("date", TodayIst),
            ("start", start), ("end", start.AddMinutes(15)), ("cur", currentCount), ("max", maxCount));
    }

    private static async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(DocslotWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
