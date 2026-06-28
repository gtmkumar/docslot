using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Phase-2 hidden-Care-Partner conversion nudge (carrot, not stick). The SECURITY DEFINER sweep
/// <c>docslot.run_partner_nudge_sweep</c> recomputes the behalf-booking funnel (distinct patients per booker in
/// 90d + broker linkage) and sends eligible "hidden partner" numbers a bilingual nudge via the outbox — once
/// per cooldown. Verified directly against the live canonical DB: a high-volume non-broker gets nudged; a
/// registered broker and a below-threshold number do not; and the cooldown prevents a second nudge.
/// </summary>
public sealed class HiddenPartnerNudgeTests
{
    private const string Conn = "Host=localhost;Port=5432;Database=docslot_platform;Username=gtmkumar";

    [Fact]
    public async Task Sweep_NudgesHiddenPartner_ExcludesBrokerAndBelowThreshold_RespectsCooldown()
    {
        var tenantId = Guid.NewGuid();
        var doctorId = Guid.NewGuid();
        var brokerId = Guid.NewGuid();
        var hidden = NewPhone();        // non-broker, 3 distinct patients → should be nudged
        var brokerPhone = NewPhone();   // a REGISTERED broker, 3 distinct patients → must NOT be nudged
        var lowVolume = NewPhone();     // non-broker, only 2 distinct patients → must NOT be nudged

        await using var conn = new NpgsqlConnection(Conn);
        await conn.OpenAsync();
        try
        {
            await Exec(conn, "INSERT INTO platform.tenants (tenant_id,tenant_code,legal_name,display_name,tenant_type,primary_email,primary_phone,status) VALUES (@id,@code,'NudgeT','Nudge Clinic','hospital','n@d.z','+919700000600','active')",
                ("id", tenantId), ("code", $"ndg-{tenantId.ToString()[..8]}"));
            await Exec(conn, "INSERT INTO docslot.healthcare_facilities (facility_id,tenant_id,facility_type,created_at,updated_at) VALUES (gen_random_uuid(),@t,'hospital',NOW(),NOW()) ON CONFLICT (tenant_id) DO NOTHING", ("t", tenantId));
            await Exec(conn, "INSERT INTO docslot.doctors (doctor_id,tenant_id,full_name,consultation_fee,is_active,is_accepting_new_patients,created_at,updated_at) VALUES (@id,@t,'Dr Nudge',500,true,true,NOW(),NOW())", ("id", doctorId), ("t", tenantId));

            // A registered broker whose phone matches the brokerPhone contact → must be excluded.
            await Exec(conn, "INSERT INTO commission.brokers (broker_id,phone,full_name,broker_type,is_active,can_refer_pndt,requires_consent_for_phi,created_at,updated_at) VALUES (@id,@ph,'Reg Broker','aggregator_agent',true,false,true,NOW(),NOW())",
                ("id", brokerId), ("ph", "+" + brokerPhone));   // stored with leading '+'; the fn matches on DIGITS (same as the wa_id contact)

            await SeedBehalfBookingsAsync(conn, tenantId, doctorId, hidden, 3);
            await SeedBehalfBookingsAsync(conn, tenantId, doctorId, brokerPhone, 3);
            await SeedBehalfBookingsAsync(conn, tenantId, doctorId, lowVolume, 2);

            // Their contact profiles (the recompute UPDATEs existing profiles — created in production on first message).
            foreach (var ph in new[] { hidden, brokerPhone, lowVolume })
                await Exec(conn, "INSERT INTO docslot.wa_contact_profiles (profile_id,tenant_id,phone,preferred_language,distinct_patients_90d,partner_nudge_count,created_at,updated_at) VALUES (gen_random_uuid(),@t,@p,'en',0,0,NOW(),NOW()) ON CONFLICT (tenant_id,phone) DO NOTHING",
                    ("t", tenantId), ("p", ph));

            // Run the sweep.
            var nudged = await ScalarAsync<int>(conn, "SELECT docslot.run_partner_nudge_sweep(3, INTERVAL '30 days')");
            Assert.True(nudged >= 1, "the hidden partner should have been nudged");

            // Funnel recomputed correctly.
            Assert.Equal(3, await ScalarAsync<int>(conn, "SELECT distinct_patients_90d FROM docslot.wa_contact_profiles WHERE tenant_id=@t AND phone=@p", ("t", tenantId), ("p", hidden)));
            Assert.Equal(2, await ScalarAsync<int>(conn, "SELECT distinct_patients_90d FROM docslot.wa_contact_profiles WHERE tenant_id=@t AND phone=@p", ("t", tenantId), ("p", lowVolume)));
            // The broker phone got linked → excluded from the funnel.
            Assert.Equal(1L, await ScalarAsync<long>(conn, "SELECT count(*) FROM docslot.wa_contact_profiles WHERE tenant_id=@t AND phone=@p AND linked_broker_id IS NOT NULL", ("t", tenantId), ("p", brokerPhone)));

            // ONLY the hidden partner got a nudge outbox + a recorded nudge.
            Assert.Equal(1L, await ScalarAsync<long>(conn, "SELECT count(*) FROM docslot.outbox_messages WHERE tenant_id=@t AND message_intent='partner_nudge' AND payload->>'to'=@p", ("t", tenantId), ("p", hidden)));
            Assert.Equal(0L, await ScalarAsync<long>(conn, "SELECT count(*) FROM docslot.outbox_messages WHERE tenant_id=@t AND message_intent='partner_nudge' AND payload->>'to' IN (@b,@l)", ("t", tenantId), ("b", brokerPhone), ("l", lowVolume)));
            Assert.Equal(1, await ScalarAsync<int>(conn, "SELECT partner_nudge_count FROM docslot.wa_contact_profiles WHERE tenant_id=@t AND phone=@p", ("t", tenantId), ("p", hidden)));

            // Cooldown: a second sweep does NOT re-nudge.
            await ScalarAsync<int>(conn, "SELECT docslot.run_partner_nudge_sweep(3, INTERVAL '30 days')");
            Assert.Equal(1, await ScalarAsync<int>(conn, "SELECT partner_nudge_count FROM docslot.wa_contact_profiles WHERE tenant_id=@t AND phone=@p", ("t", tenantId), ("p", hidden)));
            Assert.Equal(1L, await ScalarAsync<long>(conn, "SELECT count(*) FROM docslot.outbox_messages WHERE tenant_id=@t AND message_intent='partner_nudge' AND payload->>'to'=@p", ("t", tenantId), ("p", hidden)));
        }
        finally
        {
            await Exec(conn, "DELETE FROM docslot.outbox_messages WHERE tenant_id=@t", ("t", tenantId));
            await Exec(conn, "DELETE FROM docslot.wa_contact_profiles WHERE tenant_id=@t", ("t", tenantId));
            await Exec(conn, "DELETE FROM docslot.booking_status_history WHERE booking_id IN (SELECT booking_id FROM docslot.bookings WHERE tenant_id=@t)", ("t", tenantId));
            await Exec(conn, "DELETE FROM docslot.bookings WHERE tenant_id=@t", ("t", tenantId));
            await Exec(conn, "DELETE FROM docslot.time_slots WHERE tenant_id=@t", ("t", tenantId));
            await Exec(conn, "DELETE FROM docslot.doctors WHERE tenant_id=@t", ("t", tenantId));
            await Exec(conn, "DELETE FROM docslot.healthcare_facilities WHERE tenant_id=@t", ("t", tenantId));
            await Exec(conn, "DELETE FROM docslot.patient_tenant_links WHERE tenant_id=@t", ("t", tenantId));
            await Exec(conn, "DELETE FROM docslot.patients WHERE phone_number LIKE '9166%'");   // this test's patients (9166-prefixed)
            await Exec(conn, "DELETE FROM commission.brokers WHERE broker_id=@b", ("b", brokerId));
            await Exec(conn, "UPDATE platform.tenants SET deleted_at=NOW(), status='archived' WHERE tenant_id=@t", ("t", tenantId));
        }
    }

    private static async Task SeedBehalfBookingsAsync(NpgsqlConnection conn, Guid tenantId, Guid doctorId, string bookerPhone, int distinctPatients)
    {
        for (var i = 0; i < distinctPatients; i++)
        {
            var slotId = Guid.NewGuid();
            var patientId = Guid.NewGuid();
            var patientPhone = NewPhone();
            var start = new TimeOnly(8, Random.Shared.Next(0, 59), Random.Shared.Next(0, 59));
            await Exec(conn, "INSERT INTO docslot.time_slots (slot_id,tenant_id,doctor_id,slot_date,start_time,end_time,status,current_count,max_count,created_at) VALUES (@id,@t,@d,CURRENT_DATE,@s,@e,'booked',1,1,NOW())",
                ("id", slotId), ("t", tenantId), ("d", doctorId), ("s", start), ("e", start.AddMinutes(10)));
            await Exec(conn, "INSERT INTO docslot.patients (patient_id,phone_number,full_name,is_active,created_at,updated_at) VALUES (@id,@p,'Nudge Patient',true,NOW(),NOW()) ON CONFLICT (phone_number) DO NOTHING",
                ("id", patientId), ("p", patientPhone));
            await Exec(conn, "INSERT INTO docslot.bookings (booking_id,tenant_id,slot_id,patient_id,doctor_id,status,booked_via,booked_by_type,behalf_relation,behalf_booker_phone,booked_at,updated_at) VALUES (gen_random_uuid(),@t,@s,@p,@d,'completed','whatsapp','behalf','family',@bk,NOW(),NOW())",
                ("t", tenantId), ("s", slotId), ("p", patientId), ("d", doctorId), ("bk", bookerPhone));
        }
    }

    private static string NewPhone() => $"9166{Random.Shared.Next(10000000, 99999999)}";

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        var r = await cmd.ExecuteScalarAsync();
        Assert.NotNull(r);
        return (T)r!;
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
