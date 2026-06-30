using mediq.Application.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice-16 fixture for the PROACTIVE NO-SHOW PREDICTION BACKFILL. Boots the real API (running as the
/// least-privilege <c>docslot_app</c> role, so the backfill reaches the RLS-protected bookings ONLY through the
/// two SECURITY DEFINER functions). Seeds a single tenant + doctor + a FUTURE time_slot (tomorrow) + a
/// <c>confirmed</c>, not-yet-scored booking (<c>patient_consent_status='not_required'</c> ⇒ <c>is_behalf=false</c>,
/// <c>no_show_predicted_at IS NULL</c>) so the due-list scan returns it.
/// <para>
/// The AI no-show client is swapped for a <see cref="CapturingNoShowClient"/> (records each call's booking id /
/// features / service bearer; returns a fixed non-null risk) so the test can assert the worker minted a valid
/// service token. The background worker is force-OFF (default + TestHostConfig + RemoveAll&lt;IHostedService&gt;);
/// the test resolves <see cref="INoShowBackfillRunner"/> and drives <c>RunOnceAsync</c> directly — no timing
/// flakiness. Setup/teardown use a privileged role (gtmkumar) since they seed/clean across the RLS tables.
/// </para>
/// </summary>
public sealed class NoShowBackfillWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminConnString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid DoctorId { get; } = Guid.NewGuid();
    public Guid SlotId { get; } = Guid.NewGuid();
    public Guid PatientId { get; } = Guid.NewGuid();
    public Guid BookingId { get; } = Guid.NewGuid();
    public string PatientPhone { get; } = $"+9196{Random.Shared.Next(10000000, 99999999)}";

    /// <summary>The swapped-in AI client the test inspects (captured calls) — a fixed non-null risk per call.</summary>
    public CapturingNoShowClient Ai { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The worker stays OFF — the test owns the timing. A generous batch/window so the seeded booking
                // is deterministically inside the (cross-tenant) due batch regardless of other rows in the shared DB.
                ["NoShowBackfill:Enabled"] = "false",
                ["NoShowBackfill:BatchSize"] = "200",
                ["NoShowBackfill:WindowHours"] = "72",
            });
        });
        builder.ConfigureServices(services =>
        {
            // Replace whatever AI no-show client Infrastructure wired (the stub) with our capturing one.
            services.RemoveAll<IAiNoShowClient>();
            services.AddSingleton<IAiNoShowClient>(Ai);

            // Belt-and-suspenders: no background hosted services — the test fully owns the backfill timing.
            services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();
        });
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnString);
        await conn.OpenAsync();

        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (@id, @code, 'Slice16 NoShow', 'Slice16 NoShow', 'hospital', @code||'@s16.test', '+919600000016', 'active')
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            ("id", TenantId), ("code", $"s16-{TenantId.ToString()[..8]}"));

        await Exec(conn,
            "INSERT INTO docslot.doctors (doctor_id, tenant_id, full_name, is_active, is_accepting_new_patients, created_at, updated_at) VALUES (@id, @t, 'Dr Slice16', true, true, NOW(), NOW()) ON CONFLICT (doctor_id) DO NOTHING",
            ("id", DoctorId), ("t", TenantId));

        // A FUTURE slot: tomorrow 10:00 IST — always > NOW() and well within the 72h window, regardless of server TZ.
        await Exec(conn,
            "INSERT INTO docslot.time_slots (slot_id, tenant_id, doctor_id, slot_date, start_time, end_time, status, current_count, max_count, created_at) VALUES (@id, @t, @doc, CURRENT_DATE + 1, '10:00', '10:15', 'booked', 1, 1, NOW()) ON CONFLICT DO NOTHING",
            ("id", SlotId), ("t", TenantId), ("doc", DoctorId));

        await Exec(conn,
            """
            INSERT INTO docslot.patients (patient_id, phone_number, full_name, consent_given_at, consent_version, is_active, created_at, updated_at)
            VALUES (@id, @phone, 'Slice16 Patient', NOW(), 'v1', true, NOW(), NOW()) ON CONFLICT (phone_number) DO NOTHING
            """,
            ("id", PatientId), ("phone", PatientPhone));
        await Exec(conn,
            "INSERT INTO docslot.patient_tenant_links (link_id, patient_id, tenant_id, first_visit_at, last_visit_at, total_visits) VALUES (gen_random_uuid(), @p, @t, NOW(), NOW(), 0) ON CONFLICT DO NOTHING",
            ("p", PatientId), ("t", TenantId));

        // The due booking: confirmed, NOT on-behalf (is_behalf=false), not yet scored (no_show_predicted_at NULL).
        await Exec(conn,
            """
            INSERT INTO docslot.bookings
                (booking_id, tenant_id, slot_id, patient_id, doctor_id, status, patient_consent_status, booked_via, booked_for, booked_at, updated_at)
            VALUES (@id, @t, @s, @p, @doc, 'confirmed', 'not_required', 'dashboard', 'self', NOW(), NOW()) ON CONFLICT DO NOTHING
            """,
            ("id", BookingId), ("t", TenantId), ("s", SlotId), ("p", PatientId), ("doc", DoctorId));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnString);
        await conn.OpenAsync();
        await Exec(conn, "DELETE FROM docslot.bookings WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.time_slots WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.doctors WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.patient_tenant_links WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.patients WHERE patient_id=@p", ("p", PatientId));
        await Exec(conn, "UPDATE platform.tenants SET deleted_at=NOW(), status='archived' WHERE tenant_id=@t", ("t", TenantId));
        await base.DisposeAsync();
    }

    /// <summary>Reads the seeded booking's <c>no_show_predicted_at</c> marker (NULL until the backfill scores it).</summary>
    public async Task<DateTime?> ReadPredictedAtAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT no_show_predicted_at FROM docslot.bookings WHERE booking_id = @id", conn);
        cmd.Parameters.AddWithValue("id", BookingId);
        var result = await cmd.ExecuteScalarAsync();
        return result is DateTime dt ? dt : null;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A capturing test double for <see cref="IAiNoShowClient"/>: records every call (booking id, features, the
    /// service bearer the worker passed) and returns a FIXED, non-null risk so the runner always marks the booking.
    /// Thread-safe so a future parallel runner cannot corrupt the capture list.
    /// </summary>
    public sealed class CapturingNoShowClient : IAiNoShowClient
    {
        private readonly object _gate = new();
        private readonly List<CapturedCall> _calls = [];

        /// <summary>A snapshot of the calls recorded so far (safe to enumerate while the runner may still call).</summary>
        public IReadOnlyList<CapturedCall> Calls
        {
            get { lock (_gate) return _calls.ToList(); }
        }

        public Task<NoShowRisk?> PredictAsync(Guid bookingId, NoShowFeatures features, string? serviceBearer, CancellationToken ct)
        {
            lock (_gate) _calls.Add(new CapturedCall(bookingId, features, serviceBearer));
            return Task.FromResult<NoShowRisk?>(new NoShowRisk(0.42, "medium", "capturing-test", "stub-dev"));
        }
    }

    public sealed record CapturedCall(Guid BookingId, NoShowFeatures Features, string? ServiceBearer);
}
