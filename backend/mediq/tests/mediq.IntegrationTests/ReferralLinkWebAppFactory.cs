using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-2 REFERRAL-LINK click→convert fixture. Seeds a tenant + WhatsApp catalog (facility + department +
/// active doctor + future available slots) so the inbound booking flow runs, plus a broker (linked + active)
/// with a wallet + a flat ₹200 rule + an ACTIVE referral link. Wires the WhatsApp phone_number_id → tenant map
/// + App Secret so a test can drive the signed inbound flow. A patient who sends a message carrying the link's
/// short code (the prefilled /ref deep link) and books gets the booking attributed to the broker.
/// </summary>
public sealed class ReferralLinkWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ConnectionString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string AppSecret = "docslot-app-secret";
    public const string PhoneNumberId = "PNID_REFERRAL";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid DepartmentId { get; } = Guid.NewGuid();
    public Guid DoctorId { get; } = Guid.NewGuid();
    public Guid Slot1 { get; } = Guid.NewGuid();
    public Guid Slot2 { get; } = Guid.NewGuid();
    public Guid BrokerId { get; } = Guid.NewGuid();
    public Guid RuleId { get; } = Guid.NewGuid();
    public Guid LinkId { get; } = Guid.NewGuid();

    public string BrokerPhone { get; } = $"+9196{Random.Shared.Next(10000000, 99999999)}";
    public string ShortCode { get; } = $"BRK-{Guid.NewGuid():N}"[..12].ToUpperInvariant();   // BRK- + 8 hex
    public string TargetUrl => $"https://wa.me/919700000500?text=Hi%20(Ref%3A%20{ShortCode})";

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
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await Exec(conn, "INSERT INTO platform.tenants (tenant_id,tenant_code,legal_name,display_name,tenant_type,primary_email,primary_phone,status) VALUES (@id,@code,'ReferralT','Referral Clinic','hospital','rf@d.z','+919700000500','active') ON CONFLICT DO NOTHING",
            ("id", TenantId), ("code", $"rf-{TenantId.ToString()[..8]}"));
        await Exec(conn, "INSERT INTO docslot.healthcare_facilities (facility_id,tenant_id,facility_type,created_at,updated_at) VALUES (gen_random_uuid(),@t,'hospital',NOW(),NOW()) ON CONFLICT (tenant_id) DO NOTHING", ("t", TenantId));
        await Exec(conn, "INSERT INTO docslot.departments (department_id,tenant_id,name,is_active,display_order,created_at,updated_at) VALUES (@id,@t,'General Medicine',true,1,NOW(),NOW()) ON CONFLICT (tenant_id,name) DO NOTHING",
            ("id", DepartmentId), ("t", TenantId));
        await Exec(conn, "INSERT INTO docslot.doctors (doctor_id,tenant_id,full_name,department_id,specialization,consultation_fee,is_active,is_accepting_new_patients,created_at,updated_at) VALUES (@id,@t,'Dr Referral',@dep,'General Medicine',@fee,true,true,NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", DoctorId), ("t", TenantId), ("dep", DepartmentId), ("fee", ConsultationFee));
        foreach (var (sid, start) in new[] { (Slot1, new TimeOnly(11, 0)), (Slot2, new TimeOnly(11, 30)), (Guid.NewGuid(), new TimeOnly(12, 0)), (Guid.NewGuid(), new TimeOnly(12, 30)) })
            await Exec(conn, "INSERT INTO docslot.time_slots (slot_id,tenant_id,doctor_id,slot_date,start_time,end_time,status,current_count,max_count,created_at) VALUES (@id,@t,@d,@date,@s,@e,'available',0,1,NOW()) ON CONFLICT (doctor_id,slot_date,start_time) DO NOTHING",
                ("id", sid), ("t", TenantId), ("d", DoctorId), ("date", SlotDate), ("s", start), ("e", start.AddMinutes(30)));

        await Exec(conn, "INSERT INTO commission.brokers (broker_id,phone,full_name,broker_type,gst_number,gst_verified,tier_level,payout_method,is_active,can_refer_pndt,requires_consent_for_phi,created_at,updated_at) VALUES (@id,@ph,'Referral Partner','aggregator_agent','22EEEEE0000E1Z5',true,'basic','upi',true,false,true,NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", BrokerId), ("ph", BrokerPhone));
        await Exec(conn, "INSERT INTO commission.broker_tenant_links (link_id,broker_id,tenant_id,is_active,activated_at,created_at,updated_at) VALUES (gen_random_uuid(),@b,@t,true,NOW(),NOW(),NOW()) ON CONFLICT DO NOTHING", ("b", BrokerId), ("t", TenantId));
        await Exec(conn, "INSERT INTO commission.broker_wallets (broker_id,updated_at) VALUES (@b,NOW()) ON CONFLICT DO NOTHING", ("b", BrokerId));
        await Exec(conn, "INSERT INTO commission.commission_rules (rule_id,tenant_id,rule_name,rule_key,calc_type,flat_amount_inr,priority,excludes_pndt,is_active,effective_from,created_at,updated_at) VALUES (@id,@t,'Flat 200 Ref','flat_200_ref',@ct,@amt,100,true,true,NOW(),NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", RuleId), ("t", TenantId), ("ct", "flat"), ("amt", FlatCommission));

        await Exec(conn, "INSERT INTO commission.referral_links (link_id,broker_id,tenant_id,short_code,target_url,is_active,click_count,conversion_count,created_at,updated_at) VALUES (@id,@b,@t,@code,@url,true,0,0,NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", LinkId), ("b", BrokerId), ("t", TenantId), ("code", ShortCode), ("url", TargetUrl));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await Exec(conn, "DELETE FROM commission.referral_clicks WHERE link_id=@l", ("l", LinkId));
        await Exec(conn, "DELETE FROM commission.referral_links WHERE broker_id=@b", ("b", BrokerId));
        await Exec(conn, "UPDATE commission.attributions SET payout_id=NULL WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM commission.attributions WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM commission.commission_rules WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM commission.broker_tenant_links WHERE broker_id=@b", ("b", BrokerId));
        await Exec(conn, "DELETE FROM commission.broker_wallets WHERE broker_id=@b", ("b", BrokerId));
        await Exec(conn, "DELETE FROM commission.brokers WHERE broker_id=@b", ("b", BrokerId));
        await Exec(conn, "DELETE FROM docslot.outbox_messages WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.wa_message_log WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.processed_messages WHERE whatsapp_message_id LIKE @p", ("p", $"wamid.{TenantId:N}%"));
        await Exec(conn, "DELETE FROM docslot.conversations WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.wa_contact_profiles WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM platform.idempotency_keys WHERE tenant_scope=@t", ("t", TenantId.ToString()));
        await Exec(conn, "DELETE FROM docslot.opd_tokens WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.slot_holds WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.booking_status_history WHERE booking_id IN (SELECT booking_id FROM docslot.bookings WHERE tenant_id=@t)", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.bookings WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.time_slots WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.doctors WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.departments WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.healthcare_facilities WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.patient_tenant_links WHERE tenant_id=@t", ("t", TenantId));
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
