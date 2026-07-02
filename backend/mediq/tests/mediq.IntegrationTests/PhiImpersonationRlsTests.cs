using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// DB-level proof of the PHI RLS fix (audit Finding 1/2, PR #2) AND the audited-by-construction
/// impersonation guard (issue #3). Cross-tenant access to medical data must be gated by a SCOPED, AUDITED
/// impersonation session — never by the raw <c>app.is_super_admin</c> god-flag, and never by a bare
/// <c>app.impersonated_tenant</c> GUC with no <c>platform.begin_impersonation()</c> session behind it.
/// These tests run as <c>docslot_app</c> (NOBYPASSRLS) so the policies actually apply; seeding and
/// <c>begin_impersonation()</c> use the owner connection (RLS-exempt / SECURITY DEFINER).
///
/// Seeds one patient with a <c>patient_medical_history</c> row in each of three tenants
/// (A = the actor's home, B = impersonation target, C = an unrelated tenant), a support <b>actor</b> holding
/// <c>platform.users.impersonate</c>, and an OPEN audited session for the actor → tenant B. Asserts:
///   1. <c>is_super_admin=true</c> alone sees ONLY tenant A — the god-flag never opens PHI cross-tenant.
///   2. <c>actor + impersonated_tenant=B</c> (backed by a live session) opens A + B but NOT C — scoped.
///   3. the GUC is INERT without a backing session: no <c>app.user_id</c>, or a target the actor has no
///      session for (C), yields no cross-tenant PHI (issue #3 acceptance).
///   4. <c>begin_impersonation()</c> writes the hash-chained <c>audit_log</c> row.
///   5. ending the session (time-box / revocation) immediately stops the GUC resolving.
///   6. the GUCs are transaction-local: a second tx on the SAME pooled connection cannot inherit them.
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
    private readonly Guid _actorUserId = Guid.NewGuid();
    private readonly string _actorEmail = $"phi.actor+{Guid.NewGuid():N}@docslot.test";
    private readonly string _phone = "+9197" + Guid.NewGuid().ToString("N")[..8]; // unique, ≤15 chars
    private Guid _impersonationId;

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

        // A support actor holding platform.users.impersonate (via the seeded platform_support role at
        // PLATFORM scope, tenant NULL) — the only principal begin_impersonation() will accept.
        await Exec(conn,
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, email_verified, is_active, is_platform_user, created_at, updated_at)
            VALUES (@id, @email, crypt('Sup3rSecret!', gen_salt('bf', 10)), 'PHI Impersonation Actor', true, true, true, NOW(), NOW())
            ON CONFLICT (email) DO UPDATE SET deleted_at = NULL
            """,
            ("id", _actorUserId), ("email", _actorEmail));
        await Exec(conn,
            """
            INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
            SELECT gen_random_uuid(), @uid, NULL, r.role_id, false, NOW()
            FROM platform.roles r WHERE r.role_key = 'platform_support' AND r.is_system = true
            ON CONFLICT DO NOTHING
            """,
            ("uid", _actorUserId));

        // Clinic-source medical-history rows are verified at creation by definition (schema CHECK
        // chk_history_clinic_rows_verified) — stamp the verifier pair (any valid platform user; the actor above).
        foreach (var (rid, tid) in new[] { (_rowA, _tenantA), (_rowB, _tenantB), (_rowC, _tenantC) })
            await Exec(conn,
                """
                INSERT INTO docslot.patient_medical_history (history_id, patient_id, tenant_id, record_type, title, verified_by_user_id, verified_at)
                VALUES (@id, @pid, @tid, 'allergy', 'penicillin', @vby, NOW())
                ON CONFLICT (history_id) DO NOTHING
                """,
                ("id", rid), ("pid", _patientId), ("tid", tid), ("vby", _actorUserId));

        // Open the audited, time-boxed session actor → tenant B. This writes the hash-chained audit_log row
        // and is the ONLY way to make app.impersonated_tenant=B resolve for this actor.
        await using (var begin = new NpgsqlCommand(
            "SELECT platform.begin_impersonation(@actor, @target, @reason)", conn))
        {
            begin.Parameters.AddWithValue("actor", _actorUserId);
            begin.Parameters.AddWithValue("target", _tenantB);
            begin.Parameters.AddWithValue("reason", "support ticket #phi-rls");
            _impersonationId = (Guid)(await begin.ExecuteScalarAsync())!;
        }
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
    public async Task Live_Session_Opens_Only_The_Target_Tenant()
    {
        await using var conn = new NpgsqlConnection(AppConn);
        await conn.OpenAsync();

        var visible = await VisibleRowsAsync(conn,
            ("app.tenant_id", _tenantA.ToString()),
            ("app.user_id", _actorUserId.ToString()),
            ("app.impersonated_tenant", _tenantB.ToString()));

        Assert.Contains(_rowA, visible);          // own tenant
        Assert.Contains(_rowB, visible);          // the impersonated tenant — backed by a live session
        Assert.DoesNotContain(_rowC, visible);    // scoped to B — C stays hidden
    }

    [Fact]
    public async Task Impersonated_Tenant_Guc_Is_Inert_Without_A_Backing_Session()
    {
        await using var conn = new NpgsqlConnection(AppConn);
        await conn.OpenAsync();

        // (a) A bare docslot_app session sets the GUC to B but supplies NO actor id — no session matches.
        var noActor = await VisibleRowsAsync(conn,
            ("app.tenant_id", _tenantA.ToString()),
            ("app.impersonated_tenant", _tenantB.ToString()));
        Assert.Contains(_rowA, noActor);
        Assert.DoesNotContain(_rowB, noActor);    // GUC alone cannot unlock PHI (issue #3)

        // (b) The real actor points the GUC at tenant C — for which no session was ever opened.
        var wrongTarget = await VisibleRowsAsync(conn,
            ("app.tenant_id", _tenantA.ToString()),
            ("app.user_id", _actorUserId.ToString()),
            ("app.impersonated_tenant", _tenantC.ToString()));
        Assert.Contains(_rowA, wrongTarget);
        Assert.DoesNotContain(_rowC, wrongTarget); // session is scoped to B; C never resolves
    }

    [Fact]
    public async Task BeginImpersonation_Writes_The_Audit_Row()
    {
        await using var conn = new NpgsqlConnection(OwnerConn);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            SELECT count(*) FROM platform.audit_log
            WHERE action = 'impersonate'
              AND resource_type = 'tenant'
              AND resource_id = @target
              AND impersonator_user_id = @actor
              AND success = true
            """, conn);
        cmd.Parameters.AddWithValue("target", _tenantB);
        cmd.Parameters.AddWithValue("actor", _actorUserId);
        var count = (long)(await cmd.ExecuteScalarAsync())!;

        Assert.True(count >= 1, "begin_impersonation() must write an audit_log row for the support action");
    }

    [Fact]
    public async Task EndImpersonation_Closes_Session_Writes_Audit_And_Stops_Resolving()
    {
        await using var owner = new NpgsqlConnection(OwnerConn);
        await owner.OpenAsync();

        // The opening actor self-closes via the audited function (not a raw UPDATE).
        var first = await EndImpersonationAsync(owner, _impersonationId, _actorUserId);
        Assert.True(first);                                   // first close succeeds

        // Idempotent: a second close is a no-op and writes NO duplicate audit row.
        var second = await EndImpersonationAsync(owner, _impersonationId, _actorUserId);
        Assert.False(second);

        // The close is audited — exactly one end_impersonation row, mirroring the open.
        await using (var audit = new NpgsqlCommand(
            """
            SELECT count(*) FROM platform.audit_log
            WHERE action = 'end_impersonation'
              AND resource_type = 'tenant'
              AND resource_id = @target
              AND impersonator_user_id = @actor
              AND success = true
            """, owner))
        {
            audit.Parameters.AddWithValue("target", _tenantB);
            audit.Parameters.AddWithValue("actor", _actorUserId);
            Assert.Equal(1L, (long)(await audit.ExecuteScalarAsync())!);
        }

        // And the closed session no longer opens B at the RLS layer.
        await using var conn = new NpgsqlConnection(AppConn);
        await conn.OpenAsync();
        var visible = await VisibleRowsAsync(conn,
            ("app.tenant_id", _tenantA.ToString()),
            ("app.user_id", _actorUserId.ToString()),
            ("app.impersonated_tenant", _tenantB.ToString()));

        Assert.Contains(_rowA, visible);          // own tenant unaffected
        Assert.DoesNotContain(_rowB, visible);    // the ended session no longer opens B
    }

    [Fact]
    public async Task EndImpersonation_Rejects_An_Unrelated_Actor()
    {
        await using var owner = new NpgsqlConnection(OwnerConn);
        await owner.OpenAsync();

        // A different user with no platform.users.impersonate permission cannot close someone else's session.
        var stranger = Guid.NewGuid();
        await Exec(owner,
            """
            INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, is_platform_user, created_at, updated_at)
            VALUES (@id, @email, crypt('Sup3rSecret!', gen_salt('bf', 10)), 'PHI Stranger', true, false, NOW(), NOW())
            ON CONFLICT (email) DO NOTHING
            """,
            ("id", stranger), ("email", $"phi.stranger+{stranger:N}@docslot.test"));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => EndImpersonationAsync(owner, _impersonationId, stranger));
        Assert.Equal("42501", ex.SqlState);       // insufficient_privilege — session stays open

        await Exec(owner, "DELETE FROM platform.users WHERE user_id = @id", ("id", stranger));
    }

    private static async Task<bool> EndImpersonationAsync(NpgsqlConnection conn, Guid impersonationId, Guid actor)
    {
        await using var cmd = new NpgsqlCommand("SELECT platform.end_impersonation(@id, @actor)", conn);
        cmd.Parameters.AddWithValue("id", impersonationId);
        cmd.Parameters.AddWithValue("actor", actor);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Gucs_Are_Transaction_Local_No_Pool_Bleed()
    {
        await using var conn = new NpgsqlConnection(AppConn);
        await conn.OpenAsync();

        // tx1 impersonates B on this connection...
        var withImpersonation = await VisibleRowsAsync(conn,
            ("app.tenant_id", _tenantA.ToString()),
            ("app.user_id", _actorUserId.ToString()),
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
        // impersonation_sessions + audit_log are append-only (DELETE is blocked by design); leave the residue,
        // it is keyed to throwaway guids. Soft-delete the actor so its FKs from those rows stay intact.
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id = @u", ("u", _actorUserId));
        await Exec(conn, "UPDATE platform.users SET deleted_at = NOW(), is_active = false, email = @anon WHERE user_id = @u",
            ("anon", $"deleted+{_actorUserId}@phi.test"), ("u", _actorUserId));
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
