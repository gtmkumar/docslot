using mediq.Application.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Fixture for the inbound WhatsApp conversational-booking flow. Boots the real API against the live
/// canonical DB and seeds a self-contained booking graph (tenant + facility + Cardiology department + an
/// active doctor + several available future slots). Overrides the "WhatsApp" config so a test
/// <c>phone_number_id</c> maps to THIS tenant and the App Secret / verify token are known to the test (so it
/// can compute a valid <c>X-Hub-Signature-256</c>). The webhook is anonymous — no auth client needed.
/// </summary>
public sealed class WhatsAppWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";

    public const string AppSecret = "docslot-app-secret";
    public const string VerifyToken = "docslot-verify";
    public const string PhoneNumberId = "PNID_TEST_WA";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid DepartmentId { get; } = Guid.NewGuid();
    public Guid DoctorId { get; } = Guid.NewGuid();
    public Guid SlotId { get; } = Guid.NewGuid();
    public Guid SecondSlotId { get; } = Guid.NewGuid();

    /// <summary>A fresh inbound number so the conversation starts from scratch every run.</summary>
    public string FromPhone { get; } = $"9198{Random.Shared.Next(10000000, 99999999)}";

    public static readonly DateOnly SlotDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(1));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WhatsApp:VerifyToken"] = VerifyToken,
                ["WhatsApp:AppSecret"] = AppSecret,
                [$"WhatsApp:PhoneNumberIdToTenant:{PhoneNumberId}"] = TenantId.ToString(),
            });
        });
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await Exec(conn,
            """
            INSERT INTO platform.tenants (tenant_id, tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone, status)
            VALUES (@id, @code, 'WA Test Hospital', 'WA Test Hospital', 'hospital', 'wa@docslot.test', '+919000000000', 'active')
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            ("id", TenantId), ("code", $"wa-{TenantId.ToString()[..8]}"));

        await Exec(conn,
            """
            INSERT INTO docslot.healthcare_facilities (facility_id, tenant_id, facility_type, created_at, updated_at)
            VALUES (gen_random_uuid(), @tid, 'hospital', NOW(), NOW())
            ON CONFLICT (tenant_id) DO NOTHING
            """,
            ("tid", TenantId));

        await Exec(conn,
            """
            INSERT INTO docslot.departments (department_id, tenant_id, name, is_active, display_order, created_at, updated_at)
            VALUES (@id, @tid, 'Cardiology', true, 1, NOW(), NOW())
            ON CONFLICT (tenant_id, name) DO NOTHING
            """,
            ("id", DepartmentId), ("tid", TenantId));

        await Exec(conn,
            """
            INSERT INTO docslot.doctors (doctor_id, tenant_id, full_name, department_id, specialization, consultation_fee, is_active, is_accepting_new_patients, created_at, updated_at)
            VALUES (@id, @tid, 'Dr WA Cardio', @dep, 'Cardiology', 900.00, true, true, NOW(), NOW())
            ON CONFLICT (doctor_id) DO NOTHING
            """,
            ("id", DoctorId), ("tid", TenantId), ("dep", DepartmentId));

        // Two available future slots (tomorrow) so the earliest-slots list is non-empty and deterministic.
        foreach (var (sid, start) in new[] { (SlotId, new TimeOnly(11, 0)), (SecondSlotId, new TimeOnly(11, 30)) })
            await Exec(conn,
                """
                INSERT INTO docslot.time_slots (slot_id, tenant_id, doctor_id, slot_date, start_time, end_time, status, current_count, max_count, created_at)
                VALUES (@id, @tid, @doc, @date, @start, @end, 'available', 0, 1, NOW())
                ON CONFLICT (doctor_id, slot_date, start_time) DO NOTHING
                """,
                ("id", sid), ("tid", TenantId), ("doc", DoctorId), ("date", SlotDate),
                ("start", start), ("end", start.AddMinutes(30)));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        // WhatsApp artifacts first.
        await Exec(conn, "DELETE FROM docslot.wa_message_log WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.outbox_messages WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.conversations WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.wa_contact_profiles WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.processed_messages WHERE whatsapp_message_id LIKE @p", ("p", $"wamid.{TenantId:N}%"));

        // Booking graph.
        await Exec(conn, "DELETE FROM platform.idempotency_keys WHERE tenant_scope = @t", ("t", TenantId.ToString()));
        await Exec(conn, "DELETE FROM docslot.slot_holds WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.opd_tokens WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.booking_status_history WHERE booking_id IN (SELECT booking_id FROM docslot.bookings WHERE tenant_id=@t)", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.bookings WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.time_slots WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.doctors WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.departments WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.healthcare_facilities WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.patient_tenant_links WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.patients WHERE phone_number = @p", ("p", FromPhone));
        await Exec(conn, "DELETE FROM platform.purpose_of_use_log WHERE tenant_id = @t", ("t", TenantId));
        await Exec(conn, "UPDATE platform.tenants SET deleted_at = NOW(), status = 'archived' WHERE tenant_id = @t", ("t", TenantId));

        await base.DisposeAsync();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
