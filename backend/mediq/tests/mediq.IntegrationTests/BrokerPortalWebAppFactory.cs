using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-2 BROKER-PORTAL BOOKING fixture. Seeds a tenant, a broker IDENTITY whose
/// <c>commission.brokers.user_id</c> points at a real platform user holding the system <c>broker</c> role
/// (so login resolves the IDOR-safe <c>broker_id</c> claim AND the user carries
/// <c>commission.broker.create_booking_self</c>), an active broker↔tenant link + wallet, a flat ₹200 rule, a
/// doctor, and future available slots. Also wires the WhatsApp <c>phone_number_id → tenant</c> map + App Secret
/// so a test can drive the patient's signed-webhook consent reply. Each test books a fresh patient phone.
/// </summary>
public sealed class BrokerPortalWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminConnString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string Password = "Sup3rSecret!";

    public const string AppSecret = "docslot-app-secret";
    public const string PhoneNumberId = "PNID_BROKER_PORTAL";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid BrokerUserId { get; } = Guid.NewGuid();
    public Guid BrokerId { get; } = Guid.NewGuid();
    public Guid DoctorId { get; } = Guid.NewGuid();
    public Guid RuleId { get; } = Guid.NewGuid();

    public string BrokerEmail { get; } = $"broker.user+{Guid.NewGuid():N}@docslot.test";
    public string BrokerPhone { get; } = $"+9196{Random.Shared.Next(10000000, 99999999)}";

    public const decimal ConsultationFee = 500.00m;
    public const decimal FlatCommission = 200.00m;
    public static readonly DateOnly SlotDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(2));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WhatsApp:AppSecret"] = AppSecret,
                [$"WhatsApp:PhoneNumberIdToTenant:{PhoneNumberId}"] = TenantId.ToString(),
                ["Booking:MaintenanceWorkerEnabled"] = "false",
                ["WhatsApp:OutboxWorkerEnabled"] = "false",
            });
        });
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnString);
        await conn.OpenAsync();

        await Exec(conn, "INSERT INTO platform.tenants (tenant_id,tenant_code,legal_name,display_name,tenant_type,primary_email,primary_phone,status) VALUES (@id,@code,'BrokerPortalT','Broker Portal Clinic','hospital','bp@d.z','+919700000400','active') ON CONFLICT DO NOTHING",
            ("id", TenantId), ("code", $"bp-{TenantId.ToString()[..8]}"));

        // Broker USER + the system 'broker' role scoped to the tenant (→ commission.broker.create_booking_self).
        await Exec(conn, "INSERT INTO platform.users (user_id,email,password_hash,full_name,is_active,is_platform_user,created_at,updated_at) VALUES (@id,@e,crypt(@p,gen_salt('bf',10)),'Broker User',true,false,NOW(),NOW()) ON CONFLICT (email) DO UPDATE SET password_hash=EXCLUDED.password_hash, deleted_at=NULL",
            ("id", BrokerUserId), ("e", BrokerEmail), ("p", Password));
        await Exec(conn, "INSERT INTO platform.user_tenant_roles (user_tenant_role_id,user_id,tenant_id,role_id,is_primary,granted_at) SELECT gen_random_uuid(),@u,@t,role_id,true,NOW() FROM platform.roles WHERE role_key='broker' AND is_system ON CONFLICT DO NOTHING",
            ("u", BrokerUserId), ("t", TenantId));

        await Exec(conn, "INSERT INTO docslot.healthcare_facilities (facility_id,tenant_id,facility_type,created_at,updated_at) VALUES (gen_random_uuid(),@t,'hospital',NOW(),NOW()) ON CONFLICT (tenant_id) DO NOTHING", ("t", TenantId));
        await Exec(conn, "INSERT INTO docslot.doctors (doctor_id,tenant_id,full_name,consultation_fee,is_active,is_accepting_new_patients,created_at,updated_at) VALUES (@id,@t,'Dr BrokerPortal',@fee,true,true,NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", DoctorId), ("t", TenantId), ("fee", ConsultationFee));

        // Broker IDENTITY linked to the user (user_id → broker_id resolution at login) + active tenant link + wallet.
        await Exec(conn, "INSERT INTO commission.brokers (broker_id,user_id,phone,full_name,broker_type,gst_number,gst_verified,tier_level,payout_method,is_active,can_refer_pndt,requires_consent_for_phi,created_at,updated_at) VALUES (@id,@u,@ph,'Portal Partner','aggregator_agent','22DDDDD0000D1Z5',true,'basic','upi',true,false,true,NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", BrokerId), ("u", BrokerUserId), ("ph", BrokerPhone));
        await Exec(conn, "INSERT INTO commission.broker_tenant_links (link_id,broker_id,tenant_id,is_active,activated_at,created_at,updated_at) VALUES (gen_random_uuid(),@b,@t,true,NOW(),NOW(),NOW()) ON CONFLICT DO NOTHING", ("b", BrokerId), ("t", TenantId));
        await Exec(conn, "INSERT INTO commission.broker_wallets (broker_id,updated_at) VALUES (@b,NOW()) ON CONFLICT DO NOTHING", ("b", BrokerId));

        await Exec(conn, "INSERT INTO commission.commission_rules (rule_id,tenant_id,rule_name,rule_key,calc_type,flat_amount_inr,priority,excludes_pndt,is_active,effective_from,created_at,updated_at) VALUES (@id,@t,'Flat 200 Portal','flat_200_portal',@ct,@amt,100,true,true,NOW(),NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", RuleId), ("t", TenantId), ("ct", "flat"), ("amt", FlatCommission));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnString);
        await conn.OpenAsync();
        await Exec(conn, "DELETE FROM commission.attribution_claim_otps WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "UPDATE commission.attributions SET payout_id=NULL WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM commission.attributions WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM commission.commission_rules WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM commission.broker_tenant_links WHERE broker_id=@b", ("b", BrokerId));
        await Exec(conn, "DELETE FROM commission.broker_wallets WHERE broker_id=@b", ("b", BrokerId));
        await Exec(conn, "DELETE FROM commission.brokers WHERE broker_id=@b", ("b", BrokerId));
        await Exec(conn, "DELETE FROM docslot.booking_consent_otps WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.outbox_messages WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.wa_message_log WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.processed_messages WHERE whatsapp_message_id LIKE @p", ("p", $"wamid.{TenantId:N}%"));
        await Exec(conn, "DELETE FROM docslot.conversations WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.wa_contact_profiles WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.opd_tokens WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.slot_holds WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.booking_status_history WHERE booking_id IN (SELECT booking_id FROM docslot.bookings WHERE tenant_id=@t)", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.bookings WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.time_slots WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.doctors WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.healthcare_facilities WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.patient_tenant_links WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id=@u", ("u", BrokerUserId));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id=@u", ("u", BrokerUserId));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email=@e", ("e", BrokerEmail));
        await Exec(conn, "UPDATE platform.users SET deleted_at=NOW(), is_active=false, email='del+'||user_id||'@bp.test' WHERE user_id=@u", ("u", BrokerUserId));
        await Exec(conn, "UPDATE platform.tenants SET deleted_at=NOW(), status='archived' WHERE tenant_id=@t", ("t", TenantId));
        await base.DisposeAsync();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
