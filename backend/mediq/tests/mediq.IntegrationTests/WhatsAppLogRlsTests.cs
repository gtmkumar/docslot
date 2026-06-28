using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// DB-level RLS guard for the WhatsApp journal (<c>wa_message_log</c>) and outbound queue
/// (<c>outbox_messages</c>) — auditor Phase-1 Finding 2. Both carry PHI-adjacent message content (and the
/// behalf-consent OTP transits the outbox), so each must be tenant-isolated. Acting as the least-privilege
/// <c>docslot_app</c> role under tenant B's <c>app.tenant_id</c>, a tenant-A row is invisible and uninsertable;
/// the policies exist and are not <c>USING(true)</c>. The cross-tenant DRAIN worker stays functional because
/// it goes through SECURITY DEFINER functions (covered by the outbound-drain tests), never a plain app query.
/// Mirrors <see cref="BookingRlsTests"/> / <see cref="ConsentOtpComplianceTests"/>.
/// </summary>
public sealed class WhatsAppLogRlsTests : IAsyncLifetime
{
    private const string Owner = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    private const string App = "Host=localhost;Port=5432;Database=docslot_platform;Username=docslot_app";

    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _logA = Guid.NewGuid();
    private readonly Guid _outboxA = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id,tenant_code,legal_name,display_name,tenant_type,primary_email,primary_phone,status) VALUES
              (@a, @ac, 'WaLog A', 'WaLog A', 'hospital', @ae, '+919800000050', 'active'),
              (@b, @bc, 'WaLog B', 'WaLog B', 'hospital', @be, '+919800000051', 'active')
            """,
            ("a", _tenantA), ("ac", $"wla-{_tenantA.ToString()[..8]}"), ("ae", $"a+{_tenantA:N}@wlog.test"),
            ("b", _tenantB), ("bc", $"wlb-{_tenantB.ToString()[..8]}"), ("be", $"b+{_tenantB:N}@wlog.test"));

        await Exec(conn,
            """
            INSERT INTO docslot.wa_message_log (log_id, tenant_id, direction, message_type, status, sent_at)
            VALUES (@id, @a, 'outbound', 'text', 'queued', NOW())
            """,
            ("id", _logA), ("a", _tenantA));

        await Exec(conn,
            """
            INSERT INTO docslot.outbox_messages (outbox_id, tenant_id, message_intent, payload, status, next_retry_at, created_at)
            VALUES (@id, @a, 'booking_prompt', '{"to":"9198","text":"hi"}'::jsonb, 'pending', NOW(), NOW())
            """,
            ("id", _outboxA), ("a", _tenantA));
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        await Exec(conn, "DELETE FROM docslot.wa_message_log WHERE log_id = @id", ("id", _logA));
        await Exec(conn, "DELETE FROM docslot.outbox_messages WHERE outbox_id = @id", ("id", _outboxA));
        await Exec(conn, "DELETE FROM platform.tenants WHERE tenant_id = ANY(@t)", ("t", new[] { _tenantA, _tenantB }));
    }

    [Fact]
    public async Task WaMessageLog_CrossTenant_Invisible_OwnTenant_Visible()
    {
        await using (var b = await AppConnAsync(_tenantB))
            Assert.Equal(0, await ScalarAsync(b, "SELECT count(*)::int FROM docslot.wa_message_log WHERE log_id=@id", ("id", _logA)));
        await using var a = await AppConnAsync(_tenantA);
        Assert.Equal(1, await ScalarAsync(a, "SELECT count(*)::int FROM docslot.wa_message_log WHERE log_id=@id", ("id", _logA)));
    }

    [Fact]
    public async Task WaMessageLog_CrossTenant_Insert_Is_Blocked()
    {
        await using var conn = await AppConnAsync(_tenantB);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO docslot.wa_message_log (tenant_id, direction, message_type, status, sent_at) VALUES (@a,'inbound','text','received',NOW())", conn);
        cmd.Parameters.AddWithValue("a", _tenantA);
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Outbox_CrossTenant_Invisible_OwnTenant_Visible()
    {
        await using (var b = await AppConnAsync(_tenantB))
            Assert.Equal(0, await ScalarAsync(b, "SELECT count(*)::int FROM docslot.outbox_messages WHERE outbox_id=@id", ("id", _outboxA)));
        await using var a = await AppConnAsync(_tenantA);
        Assert.Equal(1, await ScalarAsync(a, "SELECT count(*)::int FROM docslot.outbox_messages WHERE outbox_id=@id", ("id", _outboxA)));
    }

    [Fact]
    public async Task Outbox_CrossTenant_Insert_Is_Blocked()
    {
        await using var conn = await AppConnAsync(_tenantB);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO docslot.outbox_messages (tenant_id, message_intent, payload, next_retry_at) VALUES (@a,'booking_prompt','{}'::jsonb, NOW())", conn);
        cmd.Parameters.AddWithValue("a", _tenantA);
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Theory]
    [InlineData("wa_message_log", "tenant_isolation_wa_message_log")]
    [InlineData("outbox_messages", "tenant_isolation_outbox_messages")]
    public async Task Policy_Exists_And_Is_Not_Permissive(string table, string policy)
    {
        await using var conn = new NpgsqlConnection(Owner);
        await conn.OpenAsync();
        var qual = await TextAsync(conn,
            "SELECT qual FROM pg_policies WHERE schemaname='docslot' AND tablename=@t AND policyname=@p",
            ("t", table), ("p", policy));
        Assert.False(string.IsNullOrWhiteSpace(qual), $"{policy} is missing on {table}");
        Assert.DoesNotContain("true", qual!.Replace("current_", ""));   // not USING(true)
        Assert.Contains("tenant", qual);
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
