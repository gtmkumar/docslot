using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.Application.Features.Docslot.Bookings;   // CreateBookingResult
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Commission;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-2 remainder: the DIRECT-BOOKING DISCOUNT (the flywheel incentive). A broker-less booking created with
/// <c>applyDirectDiscount=true</c> gets <c>direct_discount_pct</c> of the would-be commission written to
/// <c>direct_discount_inr</c> (funded from the commission pool) — and the booking becomes ineligible for any
/// broker attribution (DB trigger <c>trg_no_attribution_on_discounted</c>, closing the double-dip loophole).
/// Reuses <see cref="CommissionPipelineWebAppFactory"/> (₹500 doctor + flat ₹200 rule with the default 50%
/// direct-discount → ₹100). Each test seeds its OWN future, available slot (the fixture's are consumed).
/// </summary>
[Collection("CommissionPipeline")]
public sealed class DirectDiscountTests(CommissionPipelineWebAppFactory factory)
{
    [Fact]
    public async Task DirectDiscount_OnBrokerlessBooking_WritesHalfOfWouldBeCommission_AndBlocksAttribution()
    {
        var client = await ClientAsync(factory.SuperEmail);
        var slot0 = await SeedAvailableSlotAsync(RandomSlotTime());
        var (bookingId, slotId) = await CreateBookingWithRetryAsync(client, slot0, applyDirectDiscount: true,
            reseedSlot: () => SeedAvailableSlotAsync(RandomSlotTime()));
        try
        {
            // Flat ₹200 commission, default 50% direct discount → ₹100 written; the funding rule recorded.
            Assert.Equal(100.00m, await ScalarAsync<decimal>("SELECT direct_discount_inr FROM docslot.bookings WHERE booking_id=@b", ("b", bookingId)));
            Assert.Equal(factory.RuleId, await ScalarAsync<Guid>("SELECT direct_discount_rule_id FROM docslot.bookings WHERE booking_id=@b", ("b", bookingId)));

            // A discounted booking REJECTS a broker attribution (mutual exclusivity, DB trigger → 422).
            var attr = await client.PostAsJsonAsync("/api/v1/commission/attributions",
                new CreateAttributionRequest(bookingId, factory.BrokerId, "post_hoc_claim", null, null));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, attr.StatusCode);
            Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*)::int FROM commission.attributions WHERE booking_id=@b", ("b", bookingId)));
        }
        finally { await CleanupBookingAsync(bookingId, slotId); }
    }

    [Fact]
    public async Task NoDirectDiscount_WhenFlagNotSet_BookingStaysAttributionEligible()
    {
        var client = await ClientAsync(factory.SuperEmail);
        var slot0 = await SeedAvailableSlotAsync(RandomSlotTime());
        var (bookingId, slotId) = await CreateBookingWithRetryAsync(client, slot0, applyDirectDiscount: false,
            reseedSlot: () => SeedAvailableSlotAsync(RandomSlotTime()));
        try
        {
            Assert.Equal(0m, await ScalarAsync<decimal>("SELECT direct_discount_inr FROM docslot.bookings WHERE booking_id=@b", ("b", bookingId)));

            // No discount → the same booking CAN take a broker attribution (the trigger does not fire).
            var attr = await client.PostAsJsonAsync("/api/v1/commission/attributions",
                new CreateAttributionRequest(bookingId, factory.BrokerId, "post_hoc_claim", null, null));
            Assert.Equal(HttpStatusCode.OK, attr.StatusCode);
        }
        finally
        {
            await ExecAsync("DELETE FROM commission.attributions WHERE booking_id=@b", ("b", bookingId));
            await ExecAsync("UPDATE commission.broker_wallets SET pending_inr=0, current_month_inr=0, current_month_attributions=0, lifetime_attributions=0 WHERE broker_id=@b", ("b", factory.BrokerId));
            await CleanupBookingAsync(bookingId, slotId);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>
    /// POSTs the booking on the given (already-seeded) slot. Retries ONLY on a transient 5xx — the booking
    /// create opens a UoW and does many round-trips (hold→insert→convert→token→discount), so it's the most
    /// pool-sensitive call in the suite and can hit a transient NpgsqlException (53300) under parallel load
    /// (see the integration-test-harness "confirm-step retry" precedent). Each retry uses a FRESH idempotency
    /// key (the prior attempt cached no success) and a FRESH slot via <paramref name="reseedSlot"/> (a failed
    /// attempt may have consumed a hold on the old slot). A 4xx (validation / wrong discount) is a real logic
    /// error → it fails IMMEDIATELY, never retried, so this can't mask a backend bug.
    /// </summary>
    private async Task<(Guid BookingId, Guid SlotId)> CreateBookingWithRetryAsync(
        HttpClient client, Guid slotId, bool applyDirectDiscount, Func<Task<Guid>> reseedSlot)
    {
        HttpStatusCode lastStatus = default;
        string lastBody = "";
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var resp = await PostBookingAsync(client, slotId, applyDirectDiscount);
            if (resp.StatusCode == HttpStatusCode.Created)
                return ((await resp.Content.ReadFromJsonAsync<CreateBookingResult>())!.BookingId, slotId);

            lastStatus = resp.StatusCode;
            lastBody = await resp.Content.ReadAsStringAsync();
            // Only a transient 5xx is retryable; a 4xx is a real logic error and fails now.
            if ((int)resp.StatusCode < 500 || attempt == 3) break;

            await CleanupSlotOnlyAsync(slotId);     // drop the old slot + any orphan hold
            slotId = await reseedSlot();            // fresh slot for the next attempt
        }
        Assert.Fail($"create booking failed after retries ({(int)lastStatus}): {lastBody}");
        return default;   // unreachable
    }

    private async Task<HttpResponseMessage> PostBookingAsync(HttpClient client, Guid slotId, bool applyDirectDiscount)
    {
        var key = Guid.NewGuid().ToString();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings")
        {
            Content = JsonContent.Create(new
            {
                slotId,
                doctorId = factory.DoctorId,
                patientPhone = $"+9198{Random.Shared.Next(10000000, 99999999)}",
                patientName = "Direct Patient",
                bookingType = "consultation",
                bookedVia = "dashboard",
                issueOpdToken = false,
                idempotencyKey = key,
                applyDirectDiscount,
            }),
        };
        req.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(req);
    }

    private static async Task CleanupSlotOnlyAsync(Guid slotId)
    {
        await ExecAsync("DELETE FROM docslot.slot_holds WHERE slot_id=@s", ("s", slotId));
        await ExecAsync("DELETE FROM docslot.time_slots WHERE slot_id=@s", ("s", slotId));
    }

    // A random minute-of-day so a reseed (retry) never collides with the prior slot on (doctor, date, start_time).
    private static TimeOnly RandomSlotTime() => new(Random.Shared.Next(0, 23), Random.Shared.Next(0, 59));

    private async Task<Guid> SeedAvailableSlotAsync(TimeOnly start)
    {
        var slotId = Guid.NewGuid();
        // 3 days out clears any booking cutoff; available + capacity for a fresh create.
        await ExecAsync(
            "INSERT INTO docslot.time_slots (slot_id,tenant_id,doctor_id,slot_date,start_time,end_time,status,current_count,max_count,created_at) VALUES (@id,@t,@d,CURRENT_DATE + 3,@s,@e,'available',0,1,NOW())",
            ("id", slotId), ("t", factory.TenantId), ("d", factory.DoctorId), ("s", start), ("e", start.AddMinutes(1)));
        return slotId;
    }

    private async Task CleanupBookingAsync(Guid bookingId, Guid slotId)
    {
        await ExecAsync("DELETE FROM docslot.opd_tokens WHERE booking_id=@b", ("b", bookingId));
        await ExecAsync("DELETE FROM docslot.slot_holds WHERE slot_id=@s", ("s", slotId));
        await ExecAsync("DELETE FROM docslot.bookings WHERE booking_id=@b", ("b", bookingId));
        await ExecAsync("DELETE FROM docslot.time_slots WHERE slot_id=@s", ("s", slotId));
    }

    private async Task<HttpClient> ClientAsync(string email)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, CommissionPipelineWebAppFactory.Password, factory.TenantId));
        resp.EnsureSuccessStatusCode();
        var token = await resp.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    private static async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(CommissionPipelineWebAppFactory.AdminConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecAsync(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(CommissionPipelineWebAppFactory.AdminConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
