using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-1 consent-OTP / WhatsApp-status compliance invariants proven at the DB level against the live
/// canonical schema (mocks never catch a CHECK enum, an RLS USING predicate, or a reaper's status/interval
/// logic — real PostgreSQL does):
/// <list type="bullet">
/// <item><b>wa_message_log canonical statuses</b>: the CHECK accepts {received,queued,sent,delivered,read,
/// failed} and rejects a bogus value (PostgresException on insert as owner).</item>
/// <item><b>Consent-OTP RLS</b> (DPDP): acting as <c>docslot_app</c> under tenant B's <c>app.tenant_id</c>, a
/// booking_consent_otps row stamped for tenant A is invisible (count 0) and an INSERT stamped for tenant A is
/// blocked; the <c>tenant_isolation_booking_consent_otps</c> policy exists and is not <c>USING(true)</c>.</item>
/// <item><b>Consent-OTP expiry sweep</b>: <c>docslot.expire_stale_consent_otps()</c> expires a lapsed pending
/// OTP, cancels the awaiting behalf booking (consent → 'expired'), and frees its slot capacity.</item>
/// </list>
/// Owner connection (RLS-exempt) seeds/cleans the cross-tenant arrangement; the app connection mirrors the
/// running API's least-privilege role for the isolation assertions. Mirrors <see cref="BookingRlsTests"/>.
/// </summary>
public sealed class ConsentOtpComplianceTests : IAsyncLifetime
{
    private const string Owner = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    private const string App = "Host=localhost;Port=5432;Database=docslot_platform;Username=docslot_app";

    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _doctorA = Guid.NewGuid();
    private readonly Guid _slotA = Guid.NewGuid();
    private readonly Guid _patientA = Guid.NewGuid();
    private readonly Guid _bookingA = Guid.NewGuid();
    private readonly Guid _consentOtpA = Guid.NewGuid();
    private readonly string _patientPhoneA = $"9198{Random.Shared.Next(10000000, 99999999)}";

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();

        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id,tenant_code,legal_name,display_name,tenant_type,primary_email,primary_phone,status) VALUES
              (@a, @ac, 'Consent A', 'Consent A', 'hospital', @ae, '+919800000040', 'active'),
              (@b, @bc, 'Consent B', 'Consent B', 'hospital', @be, '+919800000041', 'active')
            """,
            ("a", _tenantA), ("ac", $"cona-{_tenantA.ToString()[..8]}"), ("ae", $"a+{_tenantA:N}@con.test"),
            ("b", _tenantB), ("bc", $"conb-{_tenantB.ToString()[..8]}"), ("be", $"b+{_tenantB:N}@con.test"));

        await Exec(conn,
            "INSERT INTO docslot.doctors (doctor_id,tenant_id,full_name,is_active) VALUES (@d,@a,'Dr A',true)",
            ("d", _doctorA), ("a", _tenantA));

        // A consumed slot (current_count=1, status='booked'), so the expiry sweep has capacity to free.
        await Exec(conn,
            """
            INSERT INTO docslot.time_slots (slot_id,tenant_id,doctor_id,slot_date,start_time,end_time,status,current_count,max_count)
            VALUES (@s,@a,@d,'2026-08-01','09:00','09:30','booked',1,1)
            """,
            ("s", _slotA), ("a", _tenantA), ("d", _doctorA));

        await Exec(conn,
            """
            INSERT INTO docslot.patients (patient_id, phone_number, full_name, age, gender, preferred_language, consent_given_at, consent_version, is_active, created_at, updated_at)
            VALUES (@p, @ph, 'Consent Patient A', 40, 'male', 'en', NOW(), 'v1', true, NOW(), NOW())
            ON CONFLICT (phone_number) DO NOTHING
            """,
            ("p", _patientA), ("ph", _patientPhoneA));

        // A pending behalf booking awaiting consent (booked_by_type='behalf', consent 'pending').
        await Exec(conn,
            """
            INSERT INTO docslot.bookings
                (booking_id, tenant_id, slot_id, patient_id, doctor_id, booking_type, status, booked_via,
                 booked_for, booked_by_type, behalf_relation, behalf_booker_phone, patient_consent_status,
                 patient_phone_at_booking, booked_at, updated_at)
            VALUES (@bid, @a, @s, @p, @d, 'consultation', 'pending', 'whatsapp',
                    'other', 'behalf', 'family', '919800000042', 'pending',
                    @ph, NOW(), NOW())
            """,
            ("bid", _bookingA), ("a", _tenantA), ("s", _slotA), ("p", _patientA), ("d", _doctorA), ("ph", _patientPhoneA));

        // An EXPIRED pending OTP for that booking (expires_at in the past) → eligible for the sweep.
        await Exec(conn,
            """
            INSERT INTO docslot.booking_consent_otps
                (consent_otp_id, tenant_id, booking_id, patient_phone, booker_phone, relation,
                 code_salt, code_hash, status, attempts, max_attempts, expires_at, created_at)
            VALUES (@oid, @a, @bid, @ph, '919800000042', 'family',
                    'salt', 'hash', 'pending', 0, 5, NOW() - interval '1 hour', NOW() - interval '2 hours')
            """,
            ("oid", _consentOtpA), ("a", _tenantA), ("bid", _bookingA), ("ph", _patientPhoneA));
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await Exec(conn, "DELETE FROM docslot.booking_consent_otps WHERE tenant_id = ANY(@t)", ("t", new[] { _tenantA, _tenantB }));
        await Exec(conn, "DELETE FROM docslot.bookings WHERE tenant_id = ANY(@t)", ("t", new[] { _tenantA, _tenantB }));
        await Exec(conn, "DELETE FROM docslot.time_slots WHERE slot_id = @s", ("s", _slotA));
        await Exec(conn, "DELETE FROM docslot.doctors WHERE doctor_id = @d", ("d", _doctorA));
        await Exec(conn, "DELETE FROM docslot.patients WHERE patient_id = @p", ("p", _patientA));
        await Exec(conn, "DELETE FROM platform.tenants WHERE tenant_id = ANY(@t)", ("t", new[] { _tenantA, _tenantB }));
    }

    // ---- Feature 5: canonical wa_message_log statuses -------------------------------------------------

    [Theory]
    [InlineData("received")]
    [InlineData("queued")]
    [InlineData("sent")]
    [InlineData("delivered")]
    [InlineData("read")]
    [InlineData("failed")]
    public async Task WaMessageLog_Accepts_Canonical_Status(string status)
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        var logId = Guid.NewGuid();
        await Exec(conn,
            """
            INSERT INTO docslot.wa_message_log (log_id, tenant_id, direction, message_type, status, sent_at)
            VALUES (@id, @t, 'inbound', 'text', @s, NOW())
            """,
            ("id", logId), ("t", _tenantA), ("s", status));
        // Clean up the accepted row.
        await Exec(conn, "DELETE FROM docslot.wa_message_log WHERE log_id = @id", ("id", logId));
    }

    [Fact]
    public async Task WaMessageLog_Rejects_Bogus_Status()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO docslot.wa_message_log (log_id, tenant_id, direction, message_type, status, sent_at)
            VALUES (@id, @t, 'inbound', 'text', 'bogus_status', NOW())
            """, conn);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("t", _tenantA);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState);   // check_violation
    }

    // ---- Feature 7: consent-OTP RLS ------------------------------------------------------------------

    [Fact]
    public async Task CrossTenant_ConsentOtp_Is_Invisible_Under_App_Role()
    {
        await using var conn = await AppConnAsync(_tenantB);   // acting as tenant B
        Assert.Equal(0, await ScalarAsync(conn,
            "SELECT count(*)::int FROM docslot.booking_consent_otps WHERE consent_otp_id=@o", ("o", _consentOtpA)));
    }

    [Fact]
    public async Task OwnTenant_ConsentOtp_Is_Visible_Under_App_Role()
    {
        await using var conn = await AppConnAsync(_tenantA);   // acting as tenant A
        Assert.Equal(1, await ScalarAsync(conn,
            "SELECT count(*)::int FROM docslot.booking_consent_otps WHERE consent_otp_id=@o", ("o", _consentOtpA)));
    }

    [Fact]
    public async Task CrossTenant_ConsentOtp_Insert_Is_Blocked_By_Rls()
    {
        await using var conn = await AppConnAsync(_tenantB);   // tenant B context
        // Insert a consent OTP stamped for tenant A → the WITH-CHECK (= USING) rejects it.
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO docslot.booking_consent_otps
                (tenant_id, booking_id, patient_phone, booker_phone, relation, code_salt, code_hash, expires_at)
            VALUES (@a, @bid, @ph, '919800000099', 'friend', 'salt', 'hash', NOW() + interval '15 minutes')
            """, conn);
        cmd.Parameters.AddWithValue("a", _tenantA);
        cmd.Parameters.AddWithValue("bid", _bookingA);
        cmd.Parameters.AddWithValue("ph", _patientPhoneA);
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task ConsentOtp_Policy_Exists_And_Is_Not_Permissive()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        var qual = await TextAsync(conn,
            "SELECT qual FROM pg_policies WHERE schemaname='docslot' AND tablename='booking_consent_otps' AND policyname='tenant_isolation_booking_consent_otps'");
        Assert.False(string.IsNullOrWhiteSpace(qual), "tenant_isolation_booking_consent_otps policy is missing");
        Assert.DoesNotContain("true", qual!.Replace("current_", ""));   // not USING(true)
        Assert.Contains("tenant", qual);                                 // references a tenant predicate
    }

    // ---- Feature 8: consent-OTP expiry sweep ---------------------------------------------------------

    [Fact]
    public async Task ExpireStaleConsentOtps_Expires_Otp_Cancels_Booking_Frees_Slot()
    {
        // Sanity precondition: the seeded slot is consumed and the OTP/booking are pending.
        await using var owner = new NpgsqlConnection(Owner);
        await owner.OpenAsync();
        Assert.Equal("pending", await TextAsync(owner, "SELECT status FROM docslot.booking_consent_otps WHERE consent_otp_id=@o", ("o", _consentOtpA)));
        Assert.Equal("pending", await TextAsync(owner, "SELECT status FROM docslot.bookings WHERE booking_id=@b", ("b", _bookingA)));

        await using var cmd = new NpgsqlCommand("SELECT docslot.expire_stale_consent_otps()", owner);
        var freed = (int)(await cmd.ExecuteScalarAsync())!;
        Assert.True(freed >= 1, "the sweep should have freed at least one slot");

        // OTP → 'expired'; booking → 'cancelled' with consent 'expired'; slot capacity freed.
        Assert.Equal("expired", await TextAsync(owner, "SELECT status FROM docslot.booking_consent_otps WHERE consent_otp_id=@o", ("o", _consentOtpA)));
        Assert.Equal("cancelled", await TextAsync(owner, "SELECT status FROM docslot.bookings WHERE booking_id=@b", ("b", _bookingA)));
        Assert.Equal("expired", await TextAsync(owner, "SELECT patient_consent_status FROM docslot.bookings WHERE booking_id=@b", ("b", _bookingA)));
        Assert.Equal(0, await ScalarAsync(owner, "SELECT current_count::int FROM docslot.time_slots WHERE slot_id=@s", ("s", _slotA)));
        Assert.Equal("available", await TextAsync(owner, "SELECT status FROM docslot.time_slots WHERE slot_id=@s", ("s", _slotA)));
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private static async Task<NpgsqlConnection> AppConnAsync(Guid tenantId)
    {
        var conn = new NpgsqlConnection(App);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", conn);
        cmd.Parameters.AddWithValue("t", tenantId.ToString());
        await cmd.ExecuteScalarAsync();
        return conn;
    }

    private static async Task<int> ScalarAsync(NpgsqlConnection conn, string sql, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string?> TextAsync(NpgsqlConnection conn, string sql, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
