using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-2 POST-HOC ATTRIBUTION-CLAIM fixture. Seeds a tenant + a super_admin (holds
/// <c>commission.attribution.claim</c>), one broker (linked + active + GST) with its own wallet, a flat ₹200
/// rule, and a doctor — plus the WhatsApp <c>phone_number_id → tenant</c> map + App Secret so a test can drive
/// the PATIENT's signed-webhook reply (the claim-OTP confirm/deny path) end-to-end. Each test seeds its OWN
/// completed booking + fresh patient phone (so the (booking,broker) attribution unique holds and the patient's
/// reply resolves cleanly), and asserts broker-wallet DELTAS around each action. Boots the API as
/// <c>docslot_app</c>; setup/teardown use the privileged <c>gtmkumar</c> owner.
/// </summary>
public sealed class ClaimAttributionWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminConnString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string AppConnString = "Host=localhost;Port=5432;Database=docslot_platform;Username=docslot_app";
    public const string Password = "Sup3rSecret!";

    public const string AppSecret = "docslot-app-secret";
    public const string VerifyToken = "docslot-verify";
    public const string PhoneNumberId = "PNID_CLAIM_TEST";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid AdminUserId { get; } = Guid.NewGuid();        // super_admin → commission.attribution.claim
    public Guid BrokerId { get; } = Guid.NewGuid();
    public Guid DoctorId { get; } = Guid.NewGuid();
    public Guid DepartmentId { get; } = Guid.NewGuid();       // for the behalf WA flow (supersede test)
    public Guid WaSlot1 { get; } = Guid.NewGuid();
    public Guid WaSlot2 { get; } = Guid.NewGuid();
    public Guid RuleId { get; } = Guid.NewGuid();

    public static readonly DateOnly WaSlotDate = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(1));

    public string AdminEmail { get; } = $"claim.admin+{Guid.NewGuid():N}@docslot.test";
    public string BrokerPhone { get; } = $"+9196{Random.Shared.Next(10000000, 99999999)}";

    public const decimal ConsultationFee = 500.00m;
    public const decimal FlatCommission = 200.00m;

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
                // The test owns timing for the no-response sweep — disable the background worker.
                ["Booking:MaintenanceWorkerEnabled"] = "false",
                ["WhatsApp:OutboxWorkerEnabled"] = "false",
            });
        });
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnString);
        await conn.OpenAsync();

        await Exec(conn, "INSERT INTO platform.tenants (tenant_id,tenant_code,legal_name,display_name,tenant_type,primary_email,primary_phone,status) VALUES (@id,@code,'ClaimT','Claim Clinic','hospital','claim@d.z','+919700000300','active') ON CONFLICT DO NOTHING",
            ("id", TenantId), ("code", $"clm-{TenantId.ToString()[..8]}"));

        await Exec(conn, "INSERT INTO platform.users (user_id,email,password_hash,full_name,is_active,is_platform_user,created_at,updated_at) VALUES (@id,@e,crypt(@p,gen_salt('bf',10)),'Claim Admin',true,true,NOW(),NOW()) ON CONFLICT (email) DO UPDATE SET password_hash=EXCLUDED.password_hash, deleted_at=NULL",
            ("id", AdminUserId), ("e", AdminEmail), ("p", Password));
        await Exec(conn, "INSERT INTO platform.user_tenant_roles (user_tenant_role_id,user_id,tenant_id,role_id,is_primary,granted_at) SELECT gen_random_uuid(),@u,NULL,role_id,true,NOW() FROM platform.roles WHERE role_key='super_admin' AND is_system ON CONFLICT DO NOTHING", ("u", AdminUserId));
        await Exec(conn, "INSERT INTO platform.user_tenant_roles (user_tenant_role_id,user_id,tenant_id,role_id,is_primary,granted_at) SELECT gen_random_uuid(),@u,@t,role_id,false,NOW() FROM platform.roles WHERE role_key='tenant_owner' AND is_system ON CONFLICT DO NOTHING", ("u", AdminUserId), ("t", TenantId));

        await Exec(conn, "INSERT INTO docslot.healthcare_facilities (facility_id,tenant_id,facility_type,created_at,updated_at) VALUES (gen_random_uuid(),@t,'hospital',NOW(),NOW()) ON CONFLICT (tenant_id) DO NOTHING", ("t", TenantId));
        await Exec(conn, "INSERT INTO docslot.departments (department_id,tenant_id,name,is_active,display_order,created_at,updated_at) VALUES (@id,@t,'General Medicine',true,1,NOW(),NOW()) ON CONFLICT (tenant_id,name) DO NOTHING",
            ("id", DepartmentId), ("t", TenantId));
        await Exec(conn, "INSERT INTO docslot.doctors (doctor_id,tenant_id,full_name,department_id,specialization,consultation_fee,is_active,is_accepting_new_patients,created_at,updated_at) VALUES (@id,@t,'Dr Claim',@dep,'General Medicine',@fee,true,true,NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", DoctorId), ("t", TenantId), ("dep", DepartmentId), ("fee", ConsultationFee));
        // Two available future slots so the behalf WA flow (the supersede-claim test) has bookable inventory.
        foreach (var (sid, start) in new[] { (WaSlot1, new TimeOnly(14, 0)), (WaSlot2, new TimeOnly(14, 30)) })
            await Exec(conn, "INSERT INTO docslot.time_slots (slot_id,tenant_id,doctor_id,slot_date,start_time,end_time,status,current_count,max_count,created_at) VALUES (@id,@t,@d,@date,@s,@e,'available',0,1,NOW()) ON CONFLICT (doctor_id,slot_date,start_time) DO NOTHING",
                ("id", sid), ("t", TenantId), ("d", DoctorId), ("date", WaSlotDate), ("s", start), ("e", start.AddMinutes(30)));

        await Exec(conn, "INSERT INTO commission.brokers (broker_id,phone,full_name,broker_type,gst_number,gst_verified,tier_level,payout_method,is_active,can_refer_pndt,requires_consent_for_phi,created_at,updated_at) VALUES (@id,@ph,'Claim Partner','aggregator_agent','22CCCCC0000C1Z5',true,'basic','upi',true,false,true,NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", BrokerId), ("ph", BrokerPhone));
        await Exec(conn, "INSERT INTO commission.broker_tenant_links (link_id,broker_id,tenant_id,is_active,activated_at,created_at,updated_at) VALUES (gen_random_uuid(),@b,@t,true,NOW(),NOW(),NOW()) ON CONFLICT DO NOTHING", ("b", BrokerId), ("t", TenantId));
        await Exec(conn, "INSERT INTO commission.broker_wallets (broker_id,updated_at) VALUES (@b,NOW()) ON CONFLICT DO NOTHING", ("b", BrokerId));

        await Exec(conn, "INSERT INTO commission.commission_rules (rule_id,tenant_id,rule_name,rule_key,calc_type,flat_amount_inr,priority,excludes_pndt,is_active,effective_from,created_at,updated_at) VALUES (@id,@t,'Flat 200 Claim','flat_200_claim',@ct,@amt,100,true,true,NOW(),NOW(),NOW()) ON CONFLICT DO NOTHING",
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
        await Exec(conn, "DELETE FROM docslot.booking_status_history WHERE booking_id IN (SELECT booking_id FROM docslot.bookings WHERE tenant_id=@t)", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.bookings WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.time_slots WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.doctors WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.departments WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.healthcare_facilities WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.patient_tenant_links WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.patients WHERE phone_number LIKE @p", ("p", "9197%claim%"));   // best-effort; explicit per-test cleanup also runs
        await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id=@u", ("u", AdminUserId));
        await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id=@u", ("u", AdminUserId));
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email=@e", ("e", AdminEmail));
        // super_admin wrote audit_log rows (FK) → SOFT-delete, never hard DELETE.
        await Exec(conn, "UPDATE platform.users SET deleted_at=NOW(), is_active=false, email='del+'||user_id||'@claim.test' WHERE user_id=@u", ("u", AdminUserId));
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
