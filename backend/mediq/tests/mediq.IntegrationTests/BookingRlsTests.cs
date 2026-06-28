using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// DB-level RLS regression guard for the Phase-0 booking + ai data-plane tables (auditor Finding 3).
/// Connects as the least-privilege <c>docslot_app</c> role (NOBYPASSRLS) — exactly as the running API does —
/// and proves a wrong-tenant row is invisible and uninsertable, independent of any app-layer filtering. Also
/// asserts every one of the 10 new policies exists and is not accidentally permissive (USING(true)).
/// Seeds/cleans across tenants via the owner connection (RLS-exempt; arrangement only).
/// </summary>
public sealed class BookingRlsTests : IAsyncLifetime
{
    private const string Owner = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    private const string App = "Host=localhost;Port=5432;Database=docslot_platform;Username=docslot_app";

    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _doctorA = Guid.NewGuid();
    private readonly Guid _slotA = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id,tenant_code,legal_name,display_name,tenant_type,primary_email,primary_phone,status) VALUES
              (@a, @ac, 'RLS A', 'RLS A', 'hospital', @ae, '+919800000020', 'active'),
              (@b, @bc, 'RLS B', 'RLS B', 'hospital', @be, '+919800000021', 'active')
            """,
            ("a", _tenantA), ("ac", $"rlsa-{_tenantA.ToString()[..8]}"), ("ae", $"a+{_tenantA:N}@rls.test"),
            ("b", _tenantB), ("bc", $"rlsb-{_tenantB.ToString()[..8]}"), ("be", $"b+{_tenantB:N}@rls.test"));
        await Exec(conn,
            "INSERT INTO docslot.doctors (doctor_id,tenant_id,full_name,is_active) VALUES (@d,@a,'Dr A',true)",
            ("d", _doctorA), ("a", _tenantA));
        await Exec(conn,
            "INSERT INTO docslot.time_slots (slot_id,tenant_id,doctor_id,slot_date,start_time,end_time) VALUES (@s,@a,@d,'2026-07-01','09:00','09:30')",
            ("s", _slotA), ("a", _tenantA), ("d", _doctorA));
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await Exec(conn, "DELETE FROM docslot.time_slots WHERE slot_id = @s", ("s", _slotA));
        await Exec(conn, "DELETE FROM docslot.doctors WHERE doctor_id = @d", ("d", _doctorA));
        await Exec(conn, "DELETE FROM platform.tenants WHERE tenant_id = ANY(@t)", ("t", new[] { _tenantA, _tenantB }));
    }

    [Fact]
    public async Task OwnTenantContext_SeesItsBookingRows()
    {
        await using var conn = await AppConnAsync(_tenantA);
        Assert.Equal(1, await ScalarAsync(conn, "SELECT count(*)::int FROM docslot.time_slots WHERE slot_id=@s", ("s", _slotA)));
    }

    [Fact]
    public async Task CrossTenantContext_CannotSeeOtherTenantsRows()
    {
        await using var conn = await AppConnAsync(_tenantB);   // acting as tenant B
        Assert.Equal(0, await ScalarAsync(conn, "SELECT count(*)::int FROM docslot.time_slots WHERE slot_id=@s", ("s", _slotA)));
    }

    [Fact]
    public async Task CrossTenantWrite_IsBlockedByRls()
    {
        await using var conn = await AppConnAsync(_tenantB);   // tenant B context
        // Insert a slot stamped for tenant A → the WITH-CHECK (= USING) rejects it.
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO docslot.time_slots (tenant_id,doctor_id,slot_date,start_time,end_time) VALUES (@a,@d,'2026-07-03','11:00','11:30')", conn);
        cmd.Parameters.AddWithValue("a", _tenantA);
        cmd.Parameters.AddWithValue("d", _doctorA);
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task EveryNewPolicyExists_AndIsNotPermissive()
    {
        var tables = new (string Schema, string Table)[]
        {
            ("docslot", "bookings"), ("docslot", "time_slots"), ("docslot", "slot_holds"),
            ("docslot", "opd_tokens"), ("docslot", "booking_status_history"),
            ("ai", "embeddings"), ("ai", "ai_predictions"), ("ai", "ai_agent_runs"),
            ("ai", "ai_document_extractions"), ("ai", "ai_agent_steps"),
        };
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        foreach (var (schema, table) in tables)
        {
            var qual = await TextAsync(conn,
                "SELECT qual FROM pg_policies WHERE schemaname=@s AND tablename=@t AND policyname LIKE 'tenant_isolation_%'",
                ("s", schema), ("t", table));
            Assert.False(string.IsNullOrWhiteSpace(qual), $"no tenant_isolation policy on {schema}.{table}");
            Assert.DoesNotContain("true", qual!.Replace("current_", ""));   // not USING(true)
            Assert.Contains("tenant", qual);                                 // references a tenant predicate
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------

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
