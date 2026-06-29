using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-2 commission MONEY-PIPELINE fixture. Unlike <see cref="CommissionWebAppFactory"/> (which seeds
/// bookings already in <c>completed</c> for the static invariants), this fixture seeds COMPLETABLE bookings in
/// <c>pending</c> so a test can drive approve→complete (earning) / cancel / no-show (reversal) through the real
/// bookings API and observe the broker wallet move. It boots the real API as the least-privilege
/// <c>docslot_app</c> role; setup/teardown use the privileged <c>gtmkumar</c> owner role.
/// <para>
/// Users: a <b>super_admin</b> (attribution.override + payouts.EXECUTE + every booking action), a
/// <b>tenant_admin</b> "finance" (payouts.APPROVE but NOT execute — approval≠execution), and a
/// <b>tenant_staff</b> "readonly" (commission.attribution.read but NOT .override — drives the 403 authz test).
/// One broker (GST-registered) with its own wallet, two completable bookings on their own slots, and one
/// active flat ₹200 rule (priority 100).
/// </para>
/// Cleanup obeys the audit-log FK trap: users that performed audited actions are SOFT-deleted, the tenant is
/// archived (never hard-DELETEd) — see the docslot-test-strategist memory "audit-log-fk-cleanup".
/// </summary>
public sealed class CommissionPipelineWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminConnString = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";
    public const string AppConnString = "Host=localhost;Port=5432;Database=docslot_platform;Username=docslot_app";
    public const string Password = "Sup3rSecret!";

    public Guid TenantId { get; } = Guid.NewGuid();
    public Guid SuperUserId { get; } = Guid.NewGuid();       // attribution.override + payouts.execute + booking actions
    public Guid FinanceUserId { get; } = Guid.NewGuid();     // tenant_admin: payouts.approve, NOT execute
    public Guid ReadonlyUserId { get; } = Guid.NewGuid();    // tenant_staff: attribution.read, NOT override
    public Guid BrokerId { get; } = Guid.NewGuid();
    public Guid DoctorId { get; } = Guid.NewGuid();
    public Guid PatientId { get; } = Guid.NewGuid();

    // Each completable booking gets its own slot so the booking lifecycle is independent across tests.
    public Guid EarnSlotId { get; } = Guid.NewGuid();
    public Guid EarnBookingId { get; } = Guid.NewGuid();      // test 1/2/3: earn → settle → pay
    public Guid CancelSlotId { get; } = Guid.NewGuid();
    public Guid CancelBookingId { get; } = Guid.NewGuid();    // test 4: earn → cancel → reverse
    public Guid DisputeSlotId { get; } = Guid.NewGuid();
    public Guid DisputeBookingId { get; } = Guid.NewGuid();   // test 5: earn → dispute clawback

    public Guid RuleId { get; } = Guid.NewGuid();

    public string SuperEmail { get; } = $"p2pipe.super+{Guid.NewGuid():N}@docslot.test";
    public string FinanceEmail { get; } = $"p2pipe.fin+{Guid.NewGuid():N}@docslot.test";
    public string ReadonlyEmail { get; } = $"p2pipe.ro+{Guid.NewGuid():N}@docslot.test";
    public string BrokerPhone { get; } = $"+9196{Random.Shared.Next(10000000, 99999999)}";
    public string PatientPhone { get; } = $"+9198{Random.Shared.Next(10000000, 99999999)}";

    public const decimal ConsultationFee = 500.00m;
    public const decimal FlatCommission = 200.00m;

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnString);
        await conn.OpenAsync();

        await Exec(conn, "INSERT INTO platform.tenants (tenant_id,tenant_code,legal_name,display_name,tenant_type,primary_email,primary_phone,status) VALUES (@id,@code,'P2Pipe','P2Pipe','hospital','p2pipe@d.z','+919700000200','active') ON CONFLICT DO NOTHING",
            ("id", TenantId), ("code", $"p2p-{TenantId.ToString()[..8]}"));

        // super_admin (platform) → all commission perms incl. payouts.execute, all booking actions.
        await SeedUser(conn, SuperUserId, SuperEmail);
        await Exec(conn, "INSERT INTO platform.user_tenant_roles (user_tenant_role_id,user_id,tenant_id,role_id,is_primary,granted_at) SELECT gen_random_uuid(),@u,NULL,role_id,true,NOW() FROM platform.roles WHERE role_key='super_admin' AND is_system ON CONFLICT DO NOTHING", ("u", SuperUserId));
        await Exec(conn, "INSERT INTO platform.user_tenant_roles (user_tenant_role_id,user_id,tenant_id,role_id,is_primary,granted_at) SELECT gen_random_uuid(),@u,@t,role_id,false,NOW() FROM platform.roles WHERE role_key='tenant_owner' AND is_system ON CONFLICT DO NOTHING", ("u", SuperUserId), ("t", TenantId));

        // finance = tenant_admin → commission.payouts.approve but NOT execute.
        await SeedUser(conn, FinanceUserId, FinanceEmail);
        await Exec(conn, "INSERT INTO platform.user_tenant_roles (user_tenant_role_id,user_id,tenant_id,role_id,is_primary,granted_at) SELECT gen_random_uuid(),@u,@t,role_id,true,NOW() FROM platform.roles WHERE role_key='tenant_admin' AND is_system ON CONFLICT DO NOTHING", ("u", FinanceUserId), ("t", TenantId));

        // readonly = tenant_staff → commission.attribution.read but NOT .override (authz 403 test).
        await SeedUser(conn, ReadonlyUserId, ReadonlyEmail);
        await Exec(conn, "INSERT INTO platform.user_tenant_roles (user_tenant_role_id,user_id,tenant_id,role_id,is_primary,granted_at) SELECT gen_random_uuid(),@u,@t,role_id,true,NOW() FROM platform.roles WHERE role_key='tenant_staff' AND is_system ON CONFLICT DO NOTHING", ("u", ReadonlyUserId), ("t", TenantId));

        // facility + doctor (₹500 consult) + a slot per completable booking.
        await Exec(conn, "INSERT INTO docslot.healthcare_facilities (facility_id,tenant_id,facility_type,created_at,updated_at) VALUES (gen_random_uuid(),@t,'hospital',NOW(),NOW()) ON CONFLICT (tenant_id) DO NOTHING", ("t", TenantId));
        await Exec(conn, "INSERT INTO docslot.doctors (doctor_id,tenant_id,full_name,consultation_fee,is_active,is_accepting_new_patients,created_at,updated_at) VALUES (@id,@t,'Dr P2',@fee,true,true,NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", DoctorId), ("t", TenantId), ("fee", ConsultationFee));
        foreach (var (sid, start) in new[] { (EarnSlotId, new TimeOnly(10, 0)), (CancelSlotId, new TimeOnly(10, 15)), (DisputeSlotId, new TimeOnly(10, 30)) })
            await Exec(conn, "INSERT INTO docslot.time_slots (slot_id,tenant_id,doctor_id,slot_date,start_time,end_time,status,current_count,max_count,created_at) VALUES (@id,@t,@d,CURRENT_DATE,@s,@e,'booked',1,1,NOW()) ON CONFLICT DO NOTHING",
                ("id", sid), ("t", TenantId), ("d", DoctorId), ("s", start), ("e", start.AddMinutes(15)));

        // patient.
        await Exec(conn, "INSERT INTO docslot.patients (patient_id,phone_number,full_name,is_active,created_at,updated_at) VALUES (@id,@p,'P2 Patient',true,NOW(),NOW()) ON CONFLICT (phone_number) DO NOTHING", ("id", PatientId), ("p", PatientPhone));

        // COMPLETABLE bookings — seeded in 'pending' (no discount) so a test can drive approve→complete / cancel / no-show.
        foreach (var (bid, sid) in new[] { (EarnBookingId, EarnSlotId), (CancelBookingId, CancelSlotId), (DisputeBookingId, DisputeSlotId) })
            await Exec(conn, "INSERT INTO docslot.bookings (booking_id,tenant_id,slot_id,patient_id,doctor_id,status,booked_via,booked_for,direct_discount_inr,booked_at,updated_at) VALUES (@id,@t,@s,@p,@d,'pending','dashboard','self',0,NOW(),NOW()) ON CONFLICT DO NOTHING",
                ("id", bid), ("t", TenantId), ("s", sid), ("p", PatientId), ("d", DoctorId));

        // broker (Care Partner), linked + active, GST-registered (so payout adds 18% GST).
        await Exec(conn, "INSERT INTO commission.brokers (broker_id,phone,full_name,broker_type,gst_number,gst_verified,tier_level,payout_method,is_active,can_refer_pndt,requires_consent_for_phi,created_at,updated_at) VALUES (@id,@ph,'Care Partner P2','aggregator_agent','22BBBBB0000B1Z5',true,'basic','upi',true,false,true,NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", BrokerId), ("ph", BrokerPhone));
        await Exec(conn, "INSERT INTO commission.broker_tenant_links (link_id,broker_id,tenant_id,is_active,activated_at,created_at,updated_at) VALUES (gen_random_uuid(),@b,@t,true,NOW(),NOW(),NOW()) ON CONFLICT DO NOTHING", ("b", BrokerId), ("t", TenantId));
        await Exec(conn, "INSERT INTO commission.broker_wallets (broker_id,updated_at) VALUES (@b,NOW()) ON CONFLICT DO NOTHING", ("b", BrokerId));

        // active flat ₹200 rule (priority 100).
        await Exec(conn, "INSERT INTO commission.commission_rules (rule_id,tenant_id,rule_name,rule_key,calc_type,flat_amount_inr,priority,excludes_pndt,is_active,effective_from,created_at,updated_at) VALUES (@id,@t,'Flat 200 P2','flat_200_p2',@ct,@amt,100,true,true,NOW(),NOW(),NOW()) ON CONFLICT DO NOTHING",
            ("id", RuleId), ("t", TenantId), ("ct", "flat"), ("amt", FlatCommission));
    }

    public new async Task DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(AdminConnString);
        await conn.OpenAsync();
        await Exec(conn, "DELETE FROM commission.attribution_disputes WHERE tenant_id=@t", ("t", TenantId));
        // attributions may reference a payout — null the link first, then delete payouts + attributions.
        await Exec(conn, "UPDATE commission.attributions SET payout_id=NULL WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM commission.tds_certificates WHERE tenant_id=@t", ("t", TenantId));   // FK→payouts (cascade) but explicit
        await Exec(conn, "DELETE FROM commission.payouts WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM commission.attributions WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM commission.broker_campaigns WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM commission.commission_rules WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM commission.referral_links WHERE broker_id=@b", ("b", BrokerId));
        await Exec(conn, "DELETE FROM commission.broker_tenant_links WHERE broker_id=@b", ("b", BrokerId));
        await Exec(conn, "DELETE FROM commission.broker_wallets WHERE broker_id=@b", ("b", BrokerId));
        await Exec(conn, "DELETE FROM commission.brokers WHERE broker_id=@b", ("b", BrokerId));
        await Exec(conn, "DELETE FROM docslot.bookings WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.time_slots WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.doctors WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.healthcare_facilities WHERE tenant_id=@t", ("t", TenantId));
        await Exec(conn, "DELETE FROM docslot.patients WHERE patient_id=@p", ("p", PatientId));
        foreach (var u in new[] { SuperUserId, FinanceUserId, ReadonlyUserId })
        {
            await Exec(conn, "DELETE FROM platform.user_tenant_roles WHERE user_id=@u", ("u", u));
            await Exec(conn, "DELETE FROM platform.user_sessions WHERE user_id=@u", ("u", u));
            // SOFT-delete: these users wrote audit_log rows (FK), so a hard DELETE would 23503 (audit-log-fk-cleanup).
            await Exec(conn, "UPDATE platform.users SET deleted_at=NOW(), is_active=false, email='del+'||user_id||'@p2pipe.test' WHERE user_id=@u", ("u", u));
        }
        await Exec(conn, "DELETE FROM platform.login_attempts WHERE email IN (@e1,@e2,@e3)", ("e1", SuperEmail), ("e2", FinanceEmail), ("e3", ReadonlyEmail));
        await Exec(conn, "UPDATE platform.tenants SET deleted_at=NOW(), status='archived' WHERE tenant_id=@t", ("t", TenantId));
        await base.DisposeAsync();
    }

    private static async Task SeedUser(NpgsqlConnection conn, Guid id, string email) =>
        await Exec(conn, "INSERT INTO platform.users (user_id,email,password_hash,full_name,is_active,is_platform_user,created_at,updated_at) VALUES (@id,@e,crypt(@p,gen_salt('bf',10)),'P2',true,true,NOW(),NOW()) ON CONFLICT (email) DO UPDATE SET password_hash=EXCLUDED.password_hash, deleted_at=NULL",
            ("id", id), ("e", email), ("p", Password));

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
