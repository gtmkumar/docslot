using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// DB-level proof of the PHI RLS fix (audit Finding 1/2, PR #2). Cross-tenant access to
/// medical data must be gated by a SCOPED impersonation (<c>app.impersonated_tenant</c>,
/// set only after <c>platform.begin_impersonation</c> audits + time-boxes it) and NEVER by
/// the raw <c>app.is_super_admin</c> god-flag. These tests run as <c>docslot_app</c>
/// (NOBYPASSRLS) so the policies actually apply; seeding uses the owner connection (RLS-exempt).
///
/// Seeds one patient with a <c>patient_medical_history</c> row in each of three tenants
/// (A = the actor's home, B = impersonation target, C = an unrelated tenant) and asserts:
///   1. <c>is_super_admin=true</c> alone sees ONLY tenant A's row — the god-flag no longer
///      opens PHI cross-tenant (the Finding 1 regression guard).
///   2. <c>impersonated_tenant=B</c> opens A + B but NOT C — impersonation is scoped, not blanket.
///   3. the GUCs are transaction-local: a second transaction on the SAME pooled connection,
///      with no impersonation, can no longer see B's row (no pool bleed).
/// </summary>
public sealed class PhiImpersonationRlsTests : IAsyncLifetime
{
    private const string OwnerConn = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    private const string AppConn = "Host=localhost;Port=5432;Database=docslot_platform;Username=docslot_app";

    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tenantC = Guid.NewGuid();
    private readonly Guid _patientId = Guid.NewGuid();
    private readonly Guid _rowA = Guid.NewGuid();
    private readonly Guid _rowB = Guid.NewGuid();
    private readonly Guid _rowC = Guid.NewGuid();
    private readonly string _phone = "+9197" + Guid.NewGuid().ToString("N")[..8]; // unique, ≤15 chars

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConn);
        await conn.OpenAsync();

        foreach (var (tid, code) in new[]
                 {
                     (_tenantA, "phi-a"), (_tenantB, "phi-b"), (_tenantC, "phi-c"),
                 })
            await Exec(conn,
                """
                INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
                VALUES (@id, @code, 'PHI RLS', 'PHI RLS', 'hospital', 'phi@docslot.test', '+919766666666', 'active')
                ON CONFLICT (tenant_id) DO NOTHING
                """,
                ("id", tid), ("code", $"{code}-{tid.ToString()[..8]}"));

        await Exec(conn,
            "INSERT INTO docslot.patients (patient_id, phone_number) VALUES (@id, @phone) ON CONFLICT (patient_id) DO NOTHING",
            ("id", _patientId), ("phone", _phone));

        foreach (var (rid, tid) in new[] { (_rowA, _tenantA), (_rowB, _tenantB), (_rowC, _tenantC) })
            await Exec(conn,
                """
                INSERT INTO docslot.patient_medical_history (history_id, patient_id, tenant_id, record_type, title)
                VALUES (@id, @pid, @tid, 'allergy', 'penicillin')
                ON CONFLICT (history_id) DO NOTHING
                """,
                ("id", rid), ("pid", _patientId), ("tid", tid));
    }

    [Fact]
    public async Task SuperAdmin_Flag_Alone_Does_Not_Open_CrossTenant_Phi()
    {
        await using var conn = new NpgsqlConnection(AppConn);
        await conn.OpenAsync();

        var visible = await VisibleRowsAsync(conn,
            ("app.tenant_id", _tenantA.ToString()),
            ("app.is_super_admin", "true"));

        Assert.Contains(_rowA, visible);          // own tenant — always
        Assert.DoesNotContain(_rowB, visible);    // god-flag must NOT reach across tenants
        Assert.DoesNotContain(_rowC, visible);
    }

    [Fact]
    public async Task Impersonation_Opens_Only_The_Target_Tenant()
    {
        await using var conn = new NpgsqlConnection(AppConn);
        await conn.OpenAsync();

        var visible = await VisibleRowsAsync(conn,
            ("app.tenant_id", _tenantA.ToString()),
            ("app.impersonated_tenant", _tenantB.ToString()));

        Assert.Contains(_rowA, visible);          // own tenant
        Assert.Contains(_rowB, visible);          // the impersonated tenant
        Assert.DoesNotContain(_rowC, visible);    // scoped to B — C stays hidden
    }

    [Fact]
    public async Task Gucs_Are_Transaction_Local_No_Pool_Bleed()
    {
        await using var conn = new NpgsqlConnection(AppConn);
        await conn.OpenAsync();

        // tx1 impersonates B on this connection...
        var withImpersonation = await VisibleRowsAsync(conn,
            ("app.tenant_id", _tenantA.ToString()),
            ("app.impersonated_tenant", _tenantB.ToString()));
        Assert.Contains(_rowB, withImpersonation);

        // ...tx2 on the SAME connection, no impersonation set, must not inherit it.
        var withoutImpersonation = await VisibleRowsAsync(conn,
            ("app.tenant_id", _tenantA.ToString()));
        Assert.DoesNotContain(_rowB, withoutImpersonation);
        Assert.Contains(_rowA, withoutImpersonation);
    }

    /// <summary>Opens a tx, applies the GUCs transaction-locally, returns which of the three seed rows are visible, then rolls back.</summary>
    private async Task<HashSet<Guid>> VisibleRowsAsync(NpgsqlConnection conn, params (string Key, string Value)[] gucs)
    {
        await using var tx = await conn.BeginTransactionAsync();
        foreach (var (key, value) in gucs)
        {
            await using var set = new NpgsqlCommand("SELECT set_config(@k, @v, true)", conn, tx);
            set.Parameters.AddWithValue("k", key);
            set.Parameters.AddWithValue("v", value);
            await set.ExecuteNonQueryAsync();
        }

        var ids = new HashSet<Guid>();
        await using (var q = new NpgsqlCommand(
            "SELECT history_id FROM docslot.patient_medical_history WHERE history_id = ANY(@ids)", conn, tx))
        {
            q.Parameters.AddWithValue("ids", new[] { _rowA, _rowB, _rowC });
            await using var rd = await q.ExecuteReaderAsync();
            while (await rd.ReadAsync()) ids.Add(rd.GetGuid(0));
        }

        await tx.RollbackAsync();
        return ids;
    }

    public async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(OwnerConn);
        await conn.OpenAsync();
        await Exec(conn, "DELETE FROM docslot.patient_medical_history WHERE history_id = ANY(@r)",
            ("r", new[] { _rowA, _rowB, _rowC }));
        await Exec(conn, "DELETE FROM docslot.patients WHERE patient_id = @p", ("p", _patientId));
        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = ANY(@t)",
            ("t", new[] { _tenantA, _tenantB, _tenantC }));
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object? Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
