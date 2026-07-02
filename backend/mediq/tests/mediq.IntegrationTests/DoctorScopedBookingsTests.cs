using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Dashboard.Dtos;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase D §6 (docs/PRESCRIPTION_CONSULTATION_PLAN.md) — GET /api/v1/bookings self-scoping. A doctor holding
/// only <c>docslot.booking.read_self</c> (the 'doctor' role) sees ONLY bookings for their own
/// <c>docslot.doctors</c> row (derived from user_id, never a client param); reception (tenant-wide
/// <c>docslot.booking.read</c>) is unchanged. Live canonical DB; owner conn seeds/cleans across PHI tables.
/// </summary>
public sealed class DoctorScopedBookingsTests(DocslotWebAppFactory factory) : IClassFixture<DocslotWebAppFactory>
{
    [Fact]
    public async Task Doctor_ReadSelf_Sees_Only_Own_Bookings_And_Cannot_Widen_Via_Query_Param()
    {
        var w = await SeedScopingWorldAsync();
        try
        {
            var doctor = await LoginClientAsync(w.DoctorEmail);

            // Unscoped list → only the doctor's OWN booking (doctorA), never doctorB's.
            var items = await ListAsync(doctor, "/api/v1/bookings");
            Assert.Contains(items, b => b.BookingId == w.BookingA);
            Assert.DoesNotContain(items, b => b.BookingId == w.BookingB);
            Assert.All(items, b => Assert.Equal(w.DoctorA, b.DoctorId));

            // Attempting to WIDEN via ?doctorId=<other doctor> must NOT work — scope is token-derived, not client-set.
            var widen = await ListAsync(doctor, $"/api/v1/bookings?doctorId={w.DoctorB}");
            Assert.Contains(widen, b => b.BookingId == w.BookingA);
            Assert.DoesNotContain(widen, b => b.BookingId == w.BookingB);
        }
        finally { await CleanupAsync(w); }
    }

    [Fact]
    public async Task Reception_TenantWide_Read_Sees_All_Bookings_Unchanged()
    {
        var w = await SeedScopingWorldAsync();
        try
        {
            // The fixture admin is tenant_owner → holds docslot.booking.read (tenant-wide). Sees BOTH doctors.
            var reception = await LoginClientAsync(factory.AdminEmail);
            var items = await ListAsync(reception, "/api/v1/bookings");
            Assert.Contains(items, b => b.BookingId == w.BookingA);
            Assert.Contains(items, b => b.BookingId == w.BookingB);
        }
        finally { await CleanupAsync(w); }
    }

    [Fact]
    public async Task Doctor_Booking_Detail_Is_Scoped_To_Own_Doctor()
    {
        var w = await SeedScopingWorldAsync();
        try
        {
            var doctor = await LoginClientAsync(w.DoctorEmail);

            // Own booking detail → 200.
            var own = await doctor.GetAsync($"/api/v1/bookings/{w.BookingA}");
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            var dto = (await own.Content.ReadFromJsonAsync<BookingListItemDto>())!;
            Assert.Equal(w.DoctorA, dto.DoctorId);

            // Another doctor's booking → 404 (out of scope, no existence leak) even though it exists in the tenant.
            var other = await doctor.GetAsync($"/api/v1/bookings/{w.BookingB}");
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);

            // Reception (tenant-wide) CAN read doctorB's booking → proves the 404 above was scoping, not absence.
            var reception = await LoginClientAsync(factory.AdminEmail);
            Assert.Equal(HttpStatusCode.OK, (await reception.GetAsync($"/api/v1/bookings/{w.BookingB}")).StatusCode);
        }
        finally { await CleanupAsync(w); }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private sealed record ScopingWorld(
        Guid DoctorUserId, string DoctorEmail, Guid DoctorA, Guid DoctorB,
        Guid SlotA, Guid SlotB, Guid BookingA, Guid BookingB);

    /// <summary>Seeds a doctor USER (the 'doctor' role → read_self only) linked to doctorA, a second doctorB, and
    /// one confirmed booking for each (same consented fixture patient).</summary>
    private async Task<ScopingWorld> SeedScopingWorldAsync()
    {
        var w = new ScopingWorld(
            Guid.NewGuid(), $"phaseD.doctor+{Guid.NewGuid():N}@docslot.test",
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await Exec(
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, is_platform_user, created_at, updated_at)
            VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'PhaseD Doctor', true, true, NOW(), NOW())
            """, ("id", w.DoctorUserId), ("email", w.DoctorEmail), ("pwd", DocslotWebAppFactory.AdminPassword));
        // 'doctor' role → holds docslot.booking.read_self, NOT docslot.booking.read (the self-scoped case).
        await Exec(
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
            FROM platform.roles r WHERE r.role_key='doctor' AND r.is_system ON CONFLICT DO NOTHING
            """, ("uid", w.DoctorUserId), ("tid", factory.TenantId));

        // doctorA is LINKED to the doctor user (user_id); doctorB is a different doctor with no linked user.
        await Exec("INSERT INTO docslot.doctors (doctor_id, tenant_id, user_id, full_name, is_active, is_accepting_new_patients, created_at, updated_at) VALUES (@id,@t,@u,'Dr A (self)',true,true,NOW(),NOW())",
            ("id", w.DoctorA), ("t", factory.TenantId), ("u", w.DoctorUserId));
        await Exec("INSERT INTO docslot.doctors (doctor_id, tenant_id, full_name, is_active, is_accepting_new_patients, created_at, updated_at) VALUES (@id,@t,'Dr B (other)',true,true,NOW(),NOW())",
            ("id", w.DoctorB), ("t", factory.TenantId));

        foreach (var (slot, doc, start) in new[] { (w.SlotA, w.DoctorA, new TimeOnly(14, 0)), (w.SlotB, w.DoctorB, new TimeOnly(14, 30)) })
            await Exec("INSERT INTO docslot.time_slots (slot_id,tenant_id,doctor_id,slot_date,start_time,end_time,status,current_count,max_count,created_at) VALUES (@s,@t,@d,@date,@st,@en,'booked',1,1,NOW())",
                ("s", slot), ("t", factory.TenantId), ("d", doc), ("date", DocslotWebAppFactory.SlotDate), ("st", start), ("en", start.AddMinutes(15)));

        foreach (var (bk, slot, doc) in new[] { (w.BookingA, w.SlotA, w.DoctorA), (w.BookingB, w.SlotB, w.DoctorB) })
            await Exec("INSERT INTO docslot.bookings (booking_id,tenant_id,slot_id,patient_id,doctor_id,status,booked_via,booked_for,booked_at,updated_at) VALUES (@b,@t,@s,@p,@d,'confirmed','dashboard','self',NOW(),NOW())",
                ("b", bk), ("s", slot), ("t", factory.TenantId), ("p", factory.PatientId), ("d", doc));

        return w;
    }

    private async Task CleanupAsync(ScopingWorld w)
    {
        await Exec("DELETE FROM docslot.bookings WHERE booking_id IN (@a,@b)", ("a", w.BookingA), ("b", w.BookingB));
        await Exec("DELETE FROM docslot.time_slots WHERE slot_id IN (@a,@b)", ("a", w.SlotA), ("b", w.SlotB));
        await Exec("DELETE FROM docslot.doctors WHERE doctor_id IN (@a,@b)", ("a", w.DoctorA), ("b", w.DoctorB));
        await Exec("DELETE FROM platform.user_tenant_roles WHERE user_id=@u", ("u", w.DoctorUserId));
        await Exec("DELETE FROM platform.user_sessions WHERE user_id=@u", ("u", w.DoctorUserId));
        await Exec("DELETE FROM platform.login_attempts WHERE email=@e", ("e", w.DoctorEmail));
        await Exec("UPDATE platform.users SET deleted_at=NOW(), is_active=false, email=@a WHERE user_id=@u",
            ("a", $"del+{w.DoctorUserId}@phaseD.test"), ("u", w.DoctorUserId));
    }

    private async Task<HttpClient> LoginClientAsync(string email)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, DocslotWebAppFactory.AdminPassword, factory.TenantId));
        resp.EnsureSuccessStatusCode();
        var token = (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private static async Task<List<BookingListItemDto>> ListAsync(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<List<BookingListItemDto>>())!;
    }

    private static async Task Exec(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(DocslotWebAppFactory.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
