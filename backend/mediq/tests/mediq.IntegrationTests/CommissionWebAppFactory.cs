using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice-07 fixture. Boots the real API (running as least-privilege docslot_app) and seeds the broker
/// economy graph: a tenant, a super_admin user (can approve AND execute payouts), a tenant_admin user (can
/// approve but NOT execute — proves approval≠execution), a broker (Care Partner), a CLEAN booking, a
/// DISCOUNTED booking (for the exclusivity test), and an active flat commission rule. Setup/teardown use the
/// privileged gtmkumar role.
/// </summary>
public sealed class CommissionWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminConnString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string Password = "Sup3rSecret!";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid SuperUserId { get; } = Guid.NewGuid();      // can approve + execute
    public Guid FinanceUserId { get; } = Guid.NewGuid();    // tenant_admin: approve but NOT execute
    public Guid BrokerId { get; } = Guid.NewGuid();
    public Guid DoctorId { get; } = Guid.NewGuid();
    public Guid PatientId { get; } = Guid.NewGuid();
    public Guid CleanSlotId { get; } = Guid.NewGuid();
    public Guid DiscountedSlotId { get; } = Guid.NewGuid();
    public Guid CleanBookingId { get; } = Guid.NewGuid();
    public Guid DiscountedBookingId { get; } = Guid.NewGuid();
    public Guid RuleId { get; } = Guid.NewGuid();
    public Guid BrokerAUserId { get; } = Guid.NewGuid();   // a broker-role user LINKED to BrokerId (broker A)
    public Guid BrokerBId { get; } = Guid.NewGuid();       // a SECOND broker (B) the broker-A user must never reach
    public string SuperEmail { get; } = $"slice07.super+{Guid.NewGuid():N}@docslot.test";
    public string FinanceEmail { get; } = $"slice07.fin+{Guid.NewGuid():N}@docslot.test";
    public string BrokerAEmail { get; } = $"slice07.brokerA+{Guid.NewGuid():N}@docslot.test";
    public string BrokerPhone { get; } = $"+9195{Random.Shared.Next(10000000, 99999999)}";
    public string BrokerBPhone { get; } = $"+9192{Random.Shared.Next(10000000, 99999999)}";
    public string PatientPhone { get; } = $"+9194{Random.Shared.Next(10000000, 99999999)}";

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnString);
        await conn.OpenAsync();

        await Exec(conn, "INSERT INTO platform.tenants (tenant_id,tenant_code,legal_name,display_name,tenant_type,primary_email,primary_phone,status) VALUES (@id,@code,'S07','S07','hospital','s07@d.z','+919700000000','active') ON CONFLICT DO NOTHING",
            ("id", TenantId), ("code", $"s07-{TenantId.ToString()[..8]}"));

        // super_admin user (platform) → all commission perms incl. payouts.execute + blacklist.
        await SeedUser(conn, SuperUserId, SuperEmail);
        await Exec(conn, "INSERT INTO platform.user_tenant_roles (user_tenant_role_id,user_id,tenant_id,role_id,is_primary,granted_at) SELECT gen_random_uuid(),@u,NULL,role_id,true,NOW() FROM platform.roles WHERE role_key='super_admin' AND is_system ON CONFLICT DO NOTHING", ("u", SuperUserId));
        await Exec(conn, "INSERT INTO platform.user_tenant_roles (user_tenant_role_id,user_id,tenant_id,role_id,is_primary,granted_at) SELECT gen_random_uuid(),@u,@t,role_id,false,NOW() FROM platform.roles WHERE role_key='tenant_owner' AND is_system ON CONFLICT DO NOTHING", ("u", SuperUserId), ("t", TenantId));

        // finance user = tenant_admin → has commission.payouts.approve but NOT execute (per seed).
        await SeedUser(conn, FinanceUserId, FinanceEmail);
        await Exec(conn, "INSERT INTO platform.user_tenant_roles (user_tenant_role_id,user_id,tenant_id,role_id,is_primary,granted_at) SELECT gen_random_uuid(),@u,@t,role_id,true,NOW() FROM platform.roles WHERE role_key='tenant_admin' AND is_system ON CONFLICT DO NOTHING", ("u", FinanceUserId), ("t", TenantId));

        // facility + doctor (₹500 consult) + 2 slots.
        await Exec(conn, "INSERT INTO docslot.healthcare_facilities (facility_id,tenant_id,facility_type,created_at,updated_at) VALUES (gen_random_uuid(),@t,'hospital',NOW(),NOW()) ON CONFLICT (tenant_id) DO NOTHING", ("t", TenantId));
        await Exec(conn, "INSERT INTO docslot.doctors (doctor_id,tenant_id,full_name,consultation_fee,is_active,is_accepting_new_patients,created_at,updated_at) VALUES (@id,@t,'Dr S07',500.00,true,true,NOW(),NOW()) ON CONFLICT DO NOTHING", ("id", DoctorId), ("t", TenantId));
        foreach (var (sid, start) in new[] { (CleanSlotId, new TimeOnly(9, 0)), (DiscountedSlotId, new TimeOnly(9, 15)) })
            await Exec(conn, "INSERT INTO docslot.time_slots (slot_id,tenant_id,doctor_id,slot_date,start_time,end_time,status,current_count,max_count,created_at) VALUES (@id,@t,@d,CURRENT_DATE,@s,@e,'booked',1,1,NOW()) ON CONFLICT DO NOTHING",
                ("id", sid), ("t", TenantId), ("d", DoctorId), ("s", start), ("e", start.AddMinutes(15)));

        // patient.
        await Exec(conn, "INSERT INTO docslot.patients (patient_id,phone_number,full_name,is_active,created_at,updated_at) VALUES (@id,@p,'S07 Patient',true,NOW(),NOW()) ON CONFLICT (phone_number) DO NOTHING", ("id", PatientId), ("p", PatientPhone));

        // CLEAN booking (no discount) + DISCOUNTED booking (direct_discount_inr > 0 → attribution blocked).
        await Exec(conn, "INSERT INTO docslot.bookings (booking_id,tenant_id,slot_id,patient_id,doctor_id,status,booked_via,booked_for,direct_discount_inr,booked_at,updated_at) VALUES (@id,@t,@s,@p,@d,'completed','dashboard','self',0,NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", CleanBookingId), ("t", TenantId), ("s", CleanSlotId), ("p", PatientId), ("d", DoctorId));
        await Exec(conn, "INSERT INTO docslot.bookings (booking_id,tenant_id,slot_id,patient_id,doctor_id,status,booked_via,booked_for,direct_discount_inr,booked_at,updated_at) VALUES (@id,@t,@s,@p,@d,'completed','dashboard','self',125.00,NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", DiscountedBookingId), ("t", TenantId), ("s", DiscountedSlotId), ("p", PatientId), ("d", DoctorId));

        // broker (Care Partner), linked + active, GST-registered for payout-with-GST test.
        await Exec(conn, "INSERT INTO commission.brokers (broker_id,phone,full_name,broker_type,gst_number,gst_verified,tier_level,payout_method,is_active,can_refer_pndt,requires_consent_for_phi,created_at,updated_at) VALUES (@id,@ph,'Care Partner S07','aggregator_agent','22AAAAA0000A1Z5',true,'basic','upi',true,false,true,NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", BrokerId), ("ph", BrokerPhone));
        await Exec(conn, "INSERT INTO commission.broker_tenant_links (link_id,broker_id,tenant_id,is_active,activated_at,created_at,updated_at) VALUES (gen_random_uuid(),@b,@t,true,NOW(),NOW(),NOW()) ON CONFLICT DO NOTHING", ("b", BrokerId), ("t", TenantId));
        await Exec(conn, "INSERT INTO commission.broker_wallets (broker_id,updated_at) VALUES (@b,NOW()) ON CONFLICT DO NOTHING", ("b", BrokerId));

        // A broker-role USER linked to broker A (commission.brokers.user_id) — proves IDOR confinement.
        await SeedUser(conn, BrokerAUserId, BrokerAEmail);
        await Exec(conn, "UPDATE commission.brokers SET user_id=@u WHERE broker_id=@b", ("u", BrokerAUserId), ("b", BrokerId));
        await Exec(conn, "INSERT INTO platform.user_tenant_roles (user_tenant_role_id,user_id,tenant_id,role_id,is_primary,granted_at) SELECT gen_random_uuid(),@u,@t,role_id,true,NOW() FROM platform.roles WHERE role_key='broker' AND is_system ON CONFLICT DO NOTHING", ("u", BrokerAUserId), ("t", TenantId));

        // A SECOND broker (B) that the broker-A user must never be able to reach.
        await Exec(conn, "INSERT INTO commission.brokers (broker_id,phone,full_name,broker_type,tier_level,payout_method,is_active,can_refer_pndt,requires_consent_for_phi,created_at,updated_at) VALUES (@id,@ph,'Care Partner B','individual','basic','upi',true,false,true,NOW(),NOW()) ON CONFLICT DO NOTHING", ("id", BrokerBId), ("ph", BrokerBPhone));
        await Exec(conn, "INSERT INTO commission.broker_tenant_links (link_id,broker_id,tenant_id,is_active,activated_at,created_at,updated_at) VALUES (gen_random_uuid(),@b,@t,true,NOW(),NOW(),NOW()) ON CONFLICT DO NOTHING", ("b", BrokerBId), ("t", TenantId));
        await Exec(conn, "INSERT INTO commission.broker_wallets (broker_id,updated_at) VALUES (@b,NOW()) ON CONFLICT DO NOTHING", ("b", BrokerBId));

        // active flat ₹200 commission rule.
        await Exec(conn, "INSERT INTO commission.commission_rules (rule_id,tenant_id,rule_name,rule_key,calc_type,flat_amount_inr,priority,excludes_pndt,is_active,effective_from,created_at,updated_at) VALUES (@id,@t,'Flat 200','flat_200','flat',200.00,100,true,true,NOW(),NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", RuleId), ("t", TenantId));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnString);
        await conn.OpenAsync();
        await Exec(conn, "DELETE FROM commission.attributions WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM commission.payouts WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM commission.commission_rules WHERE tenant_id=@t", ("t", TenantId));
        foreach (var b in new[] { BrokerId, BrokerBId })
        {
            await Exec(conn, "DELETE FROM commission.referral_links WHERE broker_id=@b", ("b", b));
            await Exec(conn, "DELETE FROM commission.broker_tenant_links WHERE broker_id=@b", ("b", b));
            await Exec(conn, "DELETE FROM commission.broker_wallets WHERE broker_id=@b", ("b", b));
        }
        await Exec(conn, "DELETE FROM platform.key_usage_log WHERE key_id IN (SELECT key_id FROM platform.encryption_keys WHERE tenant_id=@t)", ("t", TenantId));
        await Exec(conn, "DELETE FROM platform.encryption_keys WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM commission.brokers WHERE broker_id IN (@b1,@b2)", ("b1", BrokerId), ("b2", BrokerBId));
        await Exec(conn, "DELETE FROM docslot.bookings WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.time_slots WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.doctors WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.healthcare_facilities WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.patients WHERE patient_id=@p", ("p", PatientId));
        foreach (var u in new[] { SuperUserId, FinanceUserId, BrokerAUserId })
        {
            await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id=@u", ("u", u));
            await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id=@u", ("u", u));
            await Exec(conn, "UPDATE platform.users SET deleted_at=NOW(), is_active=false, email='del+'||user_id||'@s07.test' WHERE user_id=@u", ("u", u));
        }
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email IN (@e1,@e2,@e3)", ("e1", SuperEmail), ("e2", FinanceEmail), ("e3", BrokerAEmail));
        await Exec(conn, "UPDATE platform.tenants SET deleted_at=NOW(), status='archived' WHERE tenant_id=@t", ("t", TenantId));
        await base.DisposeAsync();
    }

    private static async Task SeedUser(NpgsqlConnection conn, Guid id, string email) =>
        await Exec(conn, "INSERT INTO platform.users (user_id,email,password_hash,full_name,is_active,is_platform_user,created_at,updated_at) VALUES (@id,@e,crypt(@p,gen_salt('bf',10)),'S07',true,true,NOW(),NOW()) ON CONFLICT (email) DO UPDATE SET password_hash=EXCLUDED.password_hash, deleted_at=NULL",
            ("id", id), ("e", email), ("p", Password));

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
