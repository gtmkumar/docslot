using mediq.Domain.Commission;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-2 commission guards that don't need the booted API:
/// <list type="bullet">
/// <item><b>₹100 GROSS floor</b> (pure <see cref="PayoutCalculator"/>): the minimum is applied to GROSS
/// commission, not net — a GST-registered broker whose GST top-up pushes NET above ₹100 still fails the floor
/// when GROSS is below ₹100, consistent with the <c>v_ready_payouts</c> view's <c>HAVING SUM(gross) >= 100</c>.</item>
/// <item><b>Commission tenant-isolation RLS</b> (DB-level, as the least-privilege <c>docslot_app</c> role):
/// a tenant-A attribution + payout are invisible under a tenant-B <c>app.tenant_id</c>, and an INSERT stamped
/// for tenant A from a tenant-B context is rejected. The policies exist and are NOT <c>USING(true)</c>.</item>
/// </list>
/// COMPLIANCE-flagged (payout math + tenant isolation of money rows) → requires security-compliance-auditor sign-off.
/// </summary>
public sealed class CommissionRlsAndFloorTests : IAsyncLifetime
{
    private const string Owner = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    private const string App = "Host=localhost;Port=5432;Database=docslot_platform;Username=docslot_app";

    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _brokerA = Guid.NewGuid();
    private readonly Guid _doctorA = Guid.NewGuid();
    private readonly Guid _slotA = Guid.NewGuid();
    private readonly Guid _patientA = Guid.NewGuid();
    private readonly Guid _bookingA = Guid.NewGuid();
    private readonly Guid _attributionA = Guid.NewGuid();
    private readonly Guid _payoutA = Guid.NewGuid();
    private readonly string _brokerPhone = $"+9191{Random.Shared.Next(10000000, 99999999)}";
    private readonly string _patientPhone = $"+9190{Random.Shared.Next(10000000, 99999999)}";

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id,tenant_code,legal_name,display_name,tenant_type,primary_email,primary_phone,status) VALUES
              (@a, @ac, 'C-RLS A', 'C-RLS A', 'hospital', @ae, '+919800000040', 'active'),
              (@b, @bc, 'C-RLS B', 'C-RLS B', 'hospital', @be, '+919800000041', 'active')
            """,
            ("a", _tenantA), ("ac", $"crlsa-{_tenantA.ToString()[..8]}"), ("ae", $"a+{_tenantA:N}@crls.test"),
            ("b", _tenantB), ("bc", $"crlsb-{_tenantB.ToString()[..8]}"), ("be", $"b+{_tenantB:N}@crls.test"));

        // tenant-A graph: broker (+link+wallet), doctor, slot, patient, booking, attribution, payout.
        await Exec(conn, "INSERT INTO commission.brokers (broker_id,phone,full_name,broker_type,tier_level,payout_method,is_active,can_refer_pndt,requires_consent_for_phi,created_at,updated_at) VALUES (@id,@ph,'CRLS Broker','individual','basic','upi',true,false,true,NOW(),NOW())",
            ("id", _brokerA), ("ph", _brokerPhone));
        await Exec(conn, "INSERT INTO commission.broker_tenant_links (link_id,broker_id,tenant_id,is_active,activated_at,created_at,updated_at) VALUES (gen_random_uuid(),@b,@t,true,NOW(),NOW(),NOW())", ("b", _brokerA), ("t", _tenantA));
        await Exec(conn, "INSERT INTO commission.broker_wallets (broker_id,updated_at) VALUES (@b,NOW())", ("b", _brokerA));
        await Exec(conn, "INSERT INTO docslot.doctors (doctor_id,tenant_id,full_name,consultation_fee,is_active) VALUES (@d,@a,'Dr CRLS',500.00,true)", ("d", _doctorA), ("a", _tenantA));
        await Exec(conn, "INSERT INTO docslot.time_slots (slot_id,tenant_id,doctor_id,slot_date,start_time,end_time,status,current_count,max_count) VALUES (@s,@a,@d,CURRENT_DATE,'08:00','08:15','booked',1,1)", ("s", _slotA), ("a", _tenantA), ("d", _doctorA));
        await Exec(conn, "INSERT INTO docslot.patients (patient_id,phone_number,full_name,is_active,created_at,updated_at) VALUES (@id,@p,'CRLS Patient',true,NOW(),NOW()) ON CONFLICT (phone_number) DO NOTHING", ("id", _patientA), ("p", _patientPhone));
        await Exec(conn, "INSERT INTO docslot.bookings (booking_id,tenant_id,slot_id,patient_id,doctor_id,status,booked_via,booked_for,direct_discount_inr,booked_at,updated_at) VALUES (@id,@t,@s,@p,@d,'completed','dashboard','self',0,NOW(),NOW())",
            ("id", _bookingA), ("t", _tenantA), ("s", _slotA), ("p", _patientA), ("d", _doctorA));
        await Exec(conn,
            """
            INSERT INTO commission.attributions (attribution_id, tenant_id, booking_id, broker_id, attribution_source,
                verification_status, commission_amount_inr, commission_status, attributed_at, created_at, updated_at)
            VALUES (@id, @t, @b, @br, 'referral_link', 'auto_verified', 200.00, 'ready_to_pay', NOW(), NOW(), NOW())
            """,
            ("id", _attributionA), ("t", _tenantA), ("b", _bookingA), ("br", _brokerA));
        await Exec(conn,
            """
            INSERT INTO commission.payouts (payout_id, tenant_id, broker_id, period_start, period_end, attribution_count,
                gross_amount_inr, tds_rate, tds_amount_inr, gst_rate, gst_amount_inr, net_amount_inr, status, payment_method, created_at, updated_at)
            VALUES (@id, @t, @br, CURRENT_DATE - 7, CURRENT_DATE, 1, 200.00, 5.00, 10.00, NULL, 0.00, 190.00, 'pending', 'upi', NOW(), NOW())
            """,
            ("id", _payoutA), ("t", _tenantA), ("br", _brokerA));
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await Exec(conn, "DELETE FROM commission.payouts WHERE payout_id=@p", ("p", _payoutA));
        await Exec(conn, "DELETE FROM commission.attributions WHERE attribution_id=@a", ("a", _attributionA));
        await Exec(conn, "DELETE FROM docslot.bookings WHERE booking_id=@b", ("b", _bookingA));
        await Exec(conn, "DELETE FROM docslot.time_slots WHERE slot_id=@s", ("s", _slotA));
        await Exec(conn, "DELETE FROM docslot.doctors WHERE doctor_id=@d", ("d", _doctorA));
        await Exec(conn, "DELETE FROM docslot.patients WHERE patient_id=@p", ("p", _patientA));
        await Exec(conn, "DELETE FROM commission.broker_tenant_links WHERE broker_id=@b", ("b", _brokerA));
        await Exec(conn, "DELETE FROM commission.broker_wallets WHERE broker_id=@b", ("b", _brokerA));
        await Exec(conn, "DELETE FROM commission.brokers WHERE broker_id=@b", ("b", _brokerA));
        await Exec(conn, "DELETE FROM platform.tenants WHERE tenant_id = ANY(@t)", ("t", new[] { _tenantA, _tenantB }));
    }

    // ---- 8. ₹100 GROSS floor (pure PayoutCalculator) ----------------------------------------------

    [Fact]
    public void PayoutCalculator_FloorsOnGross_NotNet()
    {
        // GST-registered, gross ₹90: TDS 4.50, GST +16.20, NET = 90 - 4.50 + 16.20 = ₹101.70 (> ₹100) —
        // yet MeetsMinimum is FALSE because the floor is on GROSS (90 < 100). This is the v_ready_payouts contract.
        var b = PayoutCalculator.Compute(90m, brokerGstRegistered: true);
        Assert.Equal(4.50m, b.TdsInr);
        Assert.Equal(16.20m, b.GstInr);
        Assert.Equal(101.70m, b.NetInr);
        Assert.True(b.NetInr > PayoutCalculator.MinimumPayoutInr);     // net clears ₹100...
        Assert.False(b.MeetsMinimum);                                  // ...but the GROSS floor still rejects it.

        // Exactly at the floor: gross ₹100 meets the minimum.
        Assert.True(PayoutCalculator.Compute(100m, brokerGstRegistered: false).MeetsMinimum);
        // Just below: gross ₹99.99 fails.
        Assert.False(PayoutCalculator.Compute(99.99m, brokerGstRegistered: false).MeetsMinimum);
    }

    // ---- 9. Commission tenant-isolation RLS (docslot_app) -----------------------------------------

    [Fact]
    public async Task OwnTenant_SeesItsAttributionAndPayout()
    {
        await using var conn = await AppConnAsync(_tenantA);
        Assert.Equal(1, await ScalarAsync(conn, "SELECT count(*)::int FROM commission.attributions WHERE attribution_id=@a", ("a", _attributionA)));
        Assert.Equal(1, await ScalarAsync(conn, "SELECT count(*)::int FROM commission.payouts WHERE payout_id=@p", ("p", _payoutA)));
    }

    [Fact]
    public async Task CrossTenant_CannotSeeOtherTenantsAttributionOrPayout()
    {
        await using var conn = await AppConnAsync(_tenantB);   // acting as tenant B
        Assert.Equal(0, await ScalarAsync(conn, "SELECT count(*)::int FROM commission.attributions WHERE attribution_id=@a", ("a", _attributionA)));
        Assert.Equal(0, await ScalarAsync(conn, "SELECT count(*)::int FROM commission.payouts WHERE payout_id=@p", ("p", _payoutA)));
    }

    [Fact]
    public async Task CrossTenant_AttributionInsertStampedForTenantA_IsBlocked()
    {
        await using var conn = await AppConnAsync(_tenantB);   // tenant B context
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO commission.attributions (attribution_id, tenant_id, booking_id, broker_id, attribution_source,
                verification_status, commission_amount_inr, commission_status, attributed_at, created_at, updated_at)
            VALUES (gen_random_uuid(), @a, @b, @br, 'referral_link', 'auto_verified', 200.00, 'pending', NOW(), NOW(), NOW())
            """, conn);
        cmd.Parameters.AddWithValue("a", _tenantA);           // stamp for tenant A → WITH-CHECK (=USING) rejects
        cmd.Parameters.AddWithValue("b", _bookingA);
        cmd.Parameters.AddWithValue("br", _brokerA);
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task TenantIsolationPolicies_Exist_AndAreNotPermissive()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        foreach (var (table, policy) in new[]
                 {
                     ("attributions", "tenant_isolation_attributions"),
                     ("payouts", "tenant_isolation_payouts"),
                 })
        {
            var qual = await TextAsync(conn,
                "SELECT qual FROM pg_policies WHERE schemaname='commission' AND tablename=@t AND policyname=@p",
                ("t", table), ("p", policy));
            Assert.False(string.IsNullOrWhiteSpace(qual), $"no {policy} policy on commission.{table}");
            Assert.DoesNotContain("true", qual!.Replace("current_", ""));   // not USING(true)
            Assert.Contains("tenant", qual);                                 // references a tenant predicate
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------

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
