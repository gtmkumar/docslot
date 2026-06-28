using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.Application.Features.Docslot.Doctors;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.Application.Abstractions;
using mediq.SharedDataModel.Docslot.Doctors;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-0 doctor schedule-management + doctor update/delete invariants against the live canonical DB:
/// (1) PUT replace-schedule persists and is read back via GET;
/// (2) replace-schedule re-materializes the rolling horizon so a scheduled weekday immediately exposes slots;
/// (3) overrides upsert + delete round-trip via the list endpoint;
/// (4) doctor profile update mutates ONLY whitelisted columns (email is NEVER touched);
/// (5) soft-delete removes the doctor from the tenant directory (and sets deleted_at, not a hard delete).
/// Cross-tenant guards are exercised implicitly by every endpoint reading tenant_id from the JWT.
/// </summary>
public sealed class DoctorScheduleManagementTests(DocslotWebAppFactory factory) : IClassFixture<DocslotWebAppFactory>
{
    [Fact]
    public async Task ReplaceSchedule_Then_Get_ReturnsTheBlocks()
    {
        var client = await AuthedClientAsync();
        var dow = (short)(int)DocslotWebAppFactory.SlotDate.DayOfWeek;

        var request = new ReplaceScheduleRequest(new[]
        {
            new ScheduleBlockDto(dow, new TimeOnly(9, 0), new TimeOnly(12, 0),
                SlotDurationMinutes: 30, MaxPatientsPerSlot: 1,
                BreakStartTime: new TimeOnly(10, 30), BreakEndTime: new TimeOnly(11, 0), IsActive: true),
        });

        var put = await client.PutAsJsonAsync($"/api/v1/doctors/{factory.DoctorId}/schedules", request);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var result = await put.Content.ReadFromJsonAsync<ReplaceScheduleResult>();
        Assert.NotNull(result);
        Assert.Equal(1, result!.BlocksSaved);

        var blocks = await client.GetFromJsonAsync<List<ScheduleBlockDto>>($"/api/v1/doctors/{factory.DoctorId}/schedules");
        Assert.NotNull(blocks);
        var block = Assert.Single(blocks!);
        Assert.Equal(dow, block.DayOfWeek);
        Assert.Equal(new TimeOnly(9, 0), block.StartTime);
        Assert.Equal(new TimeOnly(12, 0), block.EndTime);
        Assert.Equal((short)30, block.SlotDurationMinutes);
        Assert.Equal(new TimeOnly(10, 30), block.BreakStartTime);
        Assert.Equal(new TimeOnly(11, 0), block.BreakEndTime);

        // Replacing again with a single different block proves delete-then-insert (no accumulation).
        var replace = await client.PutAsJsonAsync($"/api/v1/doctors/{factory.DoctorId}/schedules",
            new ReplaceScheduleRequest(new[]
            {
                new ScheduleBlockDto(dow, new TimeOnly(14, 0), new TimeOnly(16, 0),
                    30, 1, null, null, true),
            }));
        Assert.Equal(HttpStatusCode.OK, replace.StatusCode);
        var after = await client.GetFromJsonAsync<List<ScheduleBlockDto>>($"/api/v1/doctors/{factory.DoctorId}/schedules");
        var only = Assert.Single(after!);
        Assert.Equal(new TimeOnly(14, 0), only.StartTime);
        Assert.Null(only.BreakStartTime);
    }

    [Fact]
    public async Task ReplaceSchedule_RematerializesHorizon_SlotsAppearOnScheduledWeekday()
    {
        var client = await AuthedClientAsync();
        var dow = (short)(int)DocslotWebAppFactory.SlotDate.DayOfWeek;

        // 09:00-11:00, 30-min, no break → 4 slots on the scheduled weekday (which is today, inside the horizon).
        var request = new ReplaceScheduleRequest(new[]
        {
            new ScheduleBlockDto(dow, new TimeOnly(9, 0), new TimeOnly(11, 0), 30, 1, null, null, true),
        });
        var put = await client.PutAsJsonAsync($"/api/v1/doctors/{factory.DoctorId}/schedules", request);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var result = await put.Content.ReadFromJsonAsync<ReplaceScheduleResult>();
        Assert.NotNull(result);
        Assert.True(result!.SlotsCreated > 0, "PUT replace-schedule should re-materialize the rolling horizon.");

        // The 09:00 slot is bookable today via the slots read (proves the horizon regen ran).
        var slots = await client.GetFromJsonAsync<List<SlotDto>>(
            $"/api/v1/doctors/{factory.DoctorId}/slots?date={DocslotWebAppFactory.SlotDate:yyyy-MM-dd}");
        Assert.NotNull(slots);
        Assert.Contains(slots!, s => s.StartTime == new TimeOnly(9, 0));
    }

    [Fact]
    public async Task ReplaceSchedule_WithInvalidBlock_IsRejected_422()
    {
        var client = await AuthedClientAsync();
        // end <= start → the FluentValidation rule (and DB CHECK chk_schedule_time) reject it as 422, not 500.
        var bad = new ReplaceScheduleRequest(new[]
        {
            new ScheduleBlockDto(1, new TimeOnly(12, 0), new TimeOnly(9, 0), 30, 1, null, null, true),
        });
        var resp = await client.PutAsJsonAsync($"/api/v1/doctors/{factory.DoctorId}/schedules", bad);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task Override_Upsert_Then_Delete_RoundTrips()
    {
        var client = await AuthedClientAsync();
        var date = DocslotWebAppFactory.SlotDate.AddDays(3);

        var add = await client.PostAsJsonAsync($"/api/v1/doctors/{factory.DoctorId}/schedule-overrides",
            new UpsertScheduleOverrideRequest(date, IsBlocked: true, Reason: "Conference leave"));
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var added = await add.Content.ReadFromJsonAsync<UpsertOverrideResult>();
        Assert.NotNull(added);
        Assert.NotEqual(Guid.Empty, added!.OverrideId);

        // UPSERT: posting the same date again updates in place (no duplicate, same id surfaces in the list).
        var again = await client.PostAsJsonAsync($"/api/v1/doctors/{factory.DoctorId}/schedule-overrides",
            new UpsertScheduleOverrideRequest(date, IsBlocked: false,
                CustomStartTime: new TimeOnly(15, 0), CustomEndTime: new TimeOnly(18, 0), Reason: "Half day"));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        var list = await client.GetFromJsonAsync<List<ScheduleOverrideDto>>(
            $"/api/v1/doctors/{factory.DoctorId}/schedule-overrides?from={date:yyyy-MM-dd}");
        var ovr = Assert.Single(list!, o => o.OverrideDate == date);
        Assert.False(ovr.IsBlocked);
        Assert.Equal(new TimeOnly(15, 0), ovr.CustomStartTime);
        Assert.Equal("Half day", ovr.Reason);

        // Delete removes it.
        var del = await client.DeleteAsync($"/api/v1/doctors/{factory.DoctorId}/schedule-overrides/{ovr.OverrideId}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var afterDelete = await client.GetFromJsonAsync<List<ScheduleOverrideDto>>(
            $"/api/v1/doctors/{factory.DoctorId}/schedule-overrides?from={date:yyyy-MM-dd}");
        Assert.DoesNotContain(afterDelete!, o => o.OverrideDate == date);

        // Deleting a non-existent override is a clean 404 (not a 500).
        var delMissing = await client.DeleteAsync($"/api/v1/doctors/{factory.DoctorId}/schedule-overrides/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, delMissing.StatusCode);
    }

    [Fact]
    public async Task UpdateDoctor_ChangesOnlyWhitelistedFields_EmailUntouched()
    {
        var client = await AuthedClientAsync();

        // Seed a known email directly (the seeded doctor has none, and email is never projected by a read DTO),
        // so the "email is never silently mutable" assertion is meaningful: it must survive an unrelated update.
        var knownEmail = $"protected+{Guid.NewGuid():N}@docslot.test";
        await SetDoctorEmailAsync(factory.DoctorId, knownEmail);

        var update = new UpdateDoctorRequest(
            Specialization: "Endocrinology",
            ConsultationFee: 999.00m,
            IsAcceptingNewPatients: false);
        var resp = await client.PutAsJsonAsync($"/api/v1/doctors/{factory.DoctorId}", update);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Whitelisted fields changed; email is unchanged (it was never in the request shape / not whitelisted-out).
        var emailAfter = await DoctorEmailAsync(factory.DoctorId);
        Assert.Equal(knownEmail, emailAfter);

        var (specialization, fee, accepting) = await DoctorProfileAsync(factory.DoctorId);
        Assert.Equal("Endocrinology", specialization);
        Assert.Equal(999.00m, fee);
        Assert.False(accepting);

        // An all-null body is rejected (422) — nothing to update.
        var empty = await client.PutAsJsonAsync($"/api/v1/doctors/{factory.DoctorId}", new UpdateDoctorRequest());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
    }

    [Fact]
    public async Task SoftDeleteDoctor_RemovesFromDirectory_SetsDeletedAt()
    {
        var client = await AuthedClientAsync();

        // Provision a throwaway doctor so the shared seeded doctor stays usable for other tests in the class.
        var createResp = await PostAsync(client, "/api/v1/doctors", Guid.NewGuid().ToString(),
            new CreateDoctorRequest(FullName: "Dr Disposable", DepartmentId: factory.DepartmentId));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<CreateDoctorResult>();
        Assert.NotNull(created);
        var doctorId = created!.DoctorId;

        // It is visible in the tenant directory before deletion.
        var before = await client.GetFromJsonAsync<List<DoctorDto>>("/api/v1/doctors");
        Assert.Contains(before!, d => d.DoctorId == doctorId);

        var del = await client.DeleteAsync($"/api/v1/doctors/{doctorId}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        // Gone from the directory (the list filters deleted_at IS NULL).
        var after = await client.GetFromJsonAsync<List<DoctorDto>>("/api/v1/doctors");
        Assert.DoesNotContain(after!, d => d.DoctorId == doctorId);

        // Soft delete, not hard delete: the row still exists with deleted_at set.
        Assert.True(await DoctorIsSoftDeletedAsync(doctorId), "doctor should be soft-deleted (row present, deleted_at set).");

        // A subsequent write on the now-deleted doctor fails the cross-tenant guard (deleted → ExistsInTenant false → 403).
        var reUpdate = await client.PutAsJsonAsync($"/api/v1/doctors/{doctorId}",
            new UpdateDoctorRequest(Specialization: "X"));
        Assert.Equal(HttpStatusCode.Forbidden, reUpdate.StatusCode);
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

    private static async Task SetDoctorEmailAsync(Guid doctorId, string email)
    {
        await using var conn = new NpgsqlConnection(DocslotWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE docslot.doctors SET email = @email WHERE doctor_id = @id", conn);
        cmd.Parameters.AddWithValue("email", email);
        cmd.Parameters.AddWithValue("id", doctorId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> DoctorEmailAsync(Guid doctorId)
    {
        await using var conn = new NpgsqlConnection(DocslotWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT email::text FROM docslot.doctors WHERE doctor_id = @id", conn);
        cmd.Parameters.AddWithValue("id", doctorId);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    private static async Task<(string? Specialization, decimal? Fee, bool Accepting)> DoctorProfileAsync(Guid doctorId)
    {
        await using var conn = new NpgsqlConnection(DocslotWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT specialization, consultation_fee, is_accepting_new_patients FROM docslot.doctors WHERE doctor_id = @id", conn);
        cmd.Parameters.AddWithValue("id", doctorId);
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        return (
            rd.IsDBNull(0) ? null : rd.GetString(0),
            rd.IsDBNull(1) ? null : rd.GetDecimal(1),
            rd.GetBoolean(2));
    }

    private static async Task<bool> DoctorIsSoftDeletedAsync(Guid doctorId)
    {
        await using var conn = new NpgsqlConnection(DocslotWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT deleted_at IS NOT NULL FROM docslot.doctors WHERE doctor_id = @id", conn);
        cmd.Parameters.AddWithValue("id", doctorId);
        var result = await cmd.ExecuteScalarAsync();
        return result is bool b && b;
    }
}
