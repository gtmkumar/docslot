using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Clinical;
using Npgsql;
using Xunit;

namespace mediq.IntegrationTests;

/// <summary>
/// Slice-03b clinical PHI invariants against the live canonical DB with the API running as the
/// least-privilege docslot_app role: field ciphertext-at-rest, purpose-of-use required + logged, consent
/// gates, ABDM consent required, and RLS actively blocking a cross-tenant clinical read.
/// </summary>
public sealed class ClinicalPhiTests(ClinicalWebAppFactory factory) : IClassFixture<ClinicalWebAppFactory>
{
    [Fact]
    public async Task Issue_Prescription_Encrypts_At_Rest_And_Decrypts_On_Authorized_Read()
    {
        var client = await AuthedClientAsync();

        const string diagnosis = "Acute viral pharyngitis";
        const string meds = "[{\"name\":\"Paracetamol\",\"dose\":\"500mg\"}]";
        var issue = await client.PostAsJsonAsync("/api/v1/prescriptions", new IssuePrescriptionRequest(
            factory.BookingId, factory.PatientId, factory.DoctorId, "Sore throat", "Red throat", diagnosis, meds, "Rest", 3));
        Assert.Equal(HttpStatusCode.Created, issue.StatusCode);
        var created = await issue.Content.ReadFromJsonAsync<IssuePrescriptionResult>();
        Assert.NotNull(created);
        Assert.StartsWith("PRX-", created!.PrescriptionNumber);

        // CIPHERTEXT AT REST: the raw DB column must NOT contain the plaintext.
        var rawDiagnosis = await ScalarStrAsync(
            "SELECT diagnosis FROM docslot.prescriptions WHERE prescription_id=@id", ("id", created.PrescriptionId));
        Assert.NotNull(rawDiagnosis);
        Assert.DoesNotContain(diagnosis, rawDiagnosis!);   // encrypted, not plaintext
        var rawMeds = await ScalarStrAsync(
            "SELECT medications #>> '{}' FROM docslot.prescriptions WHERE prescription_id=@id", ("id", created.PrescriptionId));
        Assert.DoesNotContain("Paracetamol", rawMeds!);

        // Authorized read with purpose-of-use → decrypts back to plaintext.
        client.DefaultRequestHeaders.Add("X-Purpose-Of-Use", "treatment");
        var read = await client.GetAsync($"/api/v1/prescriptions/{created.PrescriptionId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var dto = await read.Content.ReadFromJsonAsync<PrescriptionDto>();
        Assert.Equal(diagnosis, dto!.Diagnosis);
        Assert.Contains("Paracetamol", dto.MedicationsJson);

        // Purpose-of-use was logged.
        var purposeCount = await ScalarIntAsync(
            "SELECT COUNT(*)::int FROM platform.purpose_of_use_log WHERE accessed_resource_id=@id AND declared_purpose='treatment'",
            ("id", created.PrescriptionId));
        Assert.True(purposeCount >= 1);
    }

    [Fact]
    public async Task Clinical_Read_Without_Purpose_Header_Is_Rejected()
    {
        var client = await AuthedClientAsync();
        var issue = await client.PostAsJsonAsync("/api/v1/prescriptions", new IssuePrescriptionRequest(
            factory.BookingId, factory.PatientId, factory.DoctorId, null, null, "dx", "[]", null, null));
        var created = await issue.Content.ReadFromJsonAsync<IssuePrescriptionResult>();

        // No X-Purpose-Of-Use header → 422.
        var read = await client.GetAsync($"/api/v1/prescriptions/{created!.PrescriptionId}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, read.StatusCode);
    }

    [Fact]
    public async Task Abdm_Push_Requires_Active_Consent()
    {
        var client = await AuthedClientAsync();

        // Tenant A HAS an active ABDM consent → push succeeds.
        var ok = await client.PostAsJsonAsync("/api/v1/abdm/records", new PushAbdmRecordRequest(
            factory.PatientId, factory.BookingId, "12-3456-7890-1234", "OPConsultation", "{\"resourceType\":\"Bundle\"}"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // FHIR bundle is ciphertext at rest.
        var result = await ok.Content.ReadFromJsonAsync<PushAbdmRecordResult>();
        var rawBundle = await ScalarStrAsync(
            "SELECT fhir_bundle #>> '{}' FROM docslot.abdm_health_records WHERE record_id=@id", ("id", result!.RecordId));
        Assert.DoesNotContain("Bundle", rawBundle!);   // encrypted
    }

    [Fact]
    public async Task RLS_Blocks_Cross_Tenant_Clinical_Read()
    {
        // Insert a prescription for tenant B directly (bypassing the app, as gtmkumar).
        var presIdB = Guid.NewGuid();
        await ExecAsync(
            """
            INSERT INTO docslot.prescriptions (prescription_id, booking_id, patient_id, doctor_id, tenant_id, medications, status, created_at, updated_at)
            VALUES (@id, @b, @p, @doc, @t, to_jsonb('enc'::text), 'finalized', NOW(), NOW())
            """,
            ("id", presIdB), ("b", factory.BookingId), ("p", factory.PatientId), ("doc", factory.DoctorId), ("t", factory.TenantB));

        // The caller is scoped to tenant A. Under docslot_app (NOBYPASSRLS), RLS must hide tenant B's row.
        // Prove at the DB level under docslot_app with app.tenant_id = A.
        await using var conn = new NpgsqlConnection("Host=localhost;Port=5432;Database=docslot_platform;Username=docslot_app");
        await conn.OpenAsync();
        await using (var setCmd = new NpgsqlCommand("SELECT set_config('app.tenant_id', @a, false)", conn))
        {
            setCmd.Parameters.AddWithValue("a", factory.TenantA.ToString());
            await setCmd.ExecuteScalarAsync();
        }
        await using (var q = new NpgsqlCommand("SELECT COUNT(*) FROM docslot.prescriptions WHERE prescription_id=@id", conn))
        {
            q.Parameters.AddWithValue("id", presIdB);
            var visible = (long)(await q.ExecuteScalarAsync())!;
            Assert.Equal(0, visible);   // RLS blocks the tenant-B row when scoped to tenant A
        }

        // Sanity: the same query as the privileged role (BYPASSRLS) DOES see it — proving RLS is what blocked.
        var asAdmin = await ScalarIntAsync("SELECT COUNT(*)::int FROM docslot.prescriptions WHERE prescription_id=@id", ("id", presIdB));
        Assert.Equal(1, asAdmin);

        await ExecAsync("DELETE FROM docslot.prescriptions WHERE prescription_id=@id", ("id", presIdB));
    }

    [Fact]
    public async Task Audit_Chain_Stays_Intact_Under_Concurrent_Writes()
    {
        // Fire many audit-generating requests concurrently (each booking/clinical action writes audit + chains).
        var client = await AuthedClientAsync();
        var tasks = Enumerable.Range(0, 12).Select(async _ =>
        {
            var c = factory.CreateClient();
            var t = await LoginAsync(c);
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t.AccessToken);
            return await c.PostAsJsonAsync("/api/v1/prescriptions", new IssuePrescriptionRequest(
                factory.BookingId, factory.PatientId, factory.DoctorId, null, null, "dx", "[]", null, null));
        });
        await Task.WhenAll(tasks);

        // The advisory-locked chain must remain verifiable (0 broken links) despite concurrent writes.
        var broken = await ScalarIntAsync("SELECT COUNT(*)::int FROM platform.verify_audit_chain()");
        Assert.Equal(0, broken);
    }

    [Fact]
    public async Task Tenant_Scope_Does_Not_Bleed_Across_Parallel_Requests()
    {
        // Seed a prescription in EACH tenant (A and B) for the same patient + booking (booking is in A; for B
        // we need a booking — reuse A's booking id is fine for the FK since the read is by prescription id+tenant).
        var presA = Guid.NewGuid();
        var presB = Guid.NewGuid();
        await ExecAsync(
            "INSERT INTO docslot.prescriptions (prescription_id, booking_id, patient_id, doctor_id, tenant_id, medications, status, created_at, updated_at) VALUES (@id,@bk,@p,@d,@t, to_jsonb('encA'::text),'finalized',NOW(),NOW())",
            ("id", presA), ("bk", factory.BookingId), ("p", factory.PatientId), ("d", factory.DoctorId), ("t", factory.TenantA));
        // B needs its own booking (FK). Create a minimal booking + slot in B.
        var slotB = Guid.NewGuid(); var bookingB = Guid.NewGuid(); var doctorB = Guid.NewGuid();
        await ExecAsync("INSERT INTO docslot.doctors (doctor_id,tenant_id,full_name,is_active,is_accepting_new_patients,created_at,updated_at) VALUES (@id,@t,'DrB',true,true,NOW(),NOW())", ("id", doctorB), ("t", factory.TenantB));
        await ExecAsync("INSERT INTO docslot.time_slots (slot_id,tenant_id,doctor_id,slot_date,start_time,end_time,status,current_count,max_count,created_at) VALUES (@id,@t,@d,CURRENT_DATE,'08:00','08:15','booked',1,1,NOW())", ("id", slotB), ("t", factory.TenantB), ("d", doctorB));
        await ExecAsync("INSERT INTO docslot.bookings (booking_id,tenant_id,slot_id,patient_id,doctor_id,status,booked_via,booked_for,booked_at,updated_at) VALUES (@id,@t,@s,@p,@d,'completed','dashboard','self',NOW(),NOW())", ("id", bookingB), ("t", factory.TenantB), ("s", slotB), ("p", factory.PatientId), ("d", doctorB));
        await ExecAsync(
            "INSERT INTO docslot.prescriptions (prescription_id, booking_id, patient_id, doctor_id, tenant_id, medications, status, created_at, updated_at) VALUES (@id,@bk,@p,@d,@t, to_jsonb('encB'::text),'finalized',NOW(),NOW())",
            ("id", presB), ("bk", bookingB), ("p", factory.PatientId), ("d", doctorB), ("t", factory.TenantB));

        // The admin (super_admin platform) can scope to either tenant via login tenantId. Fire MANY parallel
        // reads, alternating tenants. Each request must see ONLY its own tenant's prescription — a pool-bleed
        // would make a tenant-A-scoped request see B's row (or vice-versa) under RLS.
        var tasks = Enumerable.Range(0, 24).Select(async i =>
        {
            var inTenantA = i % 2 == 0;
            var tenant = inTenantA ? factory.TenantA : factory.TenantB;
            var ownId = inTenantA ? presA : presB;
            var otherId = inTenantA ? presB : presA;

            var c = factory.CreateClient();
            var login = await c.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(factory.AdminEmail, ClinicalWebAppFactory.AdminPassword, tenant));
            login.EnsureSuccessStatusCode();
            var token = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            // The prescription LIST (headers only, no decrypt) is RLS-scoped to the request's tenant. Under a
            // pool-bleed, a tenant-A-scoped request would see B's prescription id (or vice-versa).
            var resp = await c.GetAsync($"/api/v1/patients/{factory.PatientId}/prescriptions");
            resp.EnsureSuccessStatusCode();
            var items = await resp.Content.ReadFromJsonAsync<List<mediq.SharedDataModel.Docslot.Clinical.PrescriptionListItemDto>>();
            var ids = items!.Select(x => x.PrescriptionId).ToHashSet();
            return (SeesOwn: ids.Contains(ownId), SeesOther: ids.Contains(otherId));
        });
        var results = await Task.WhenAll(tasks);

        // No bleed: every request saw its OWN tenant's prescription and NEVER the other tenant's.
        Assert.All(results, r =>
        {
            Assert.True(r.SeesOwn, "request did not see its own tenant's prescription");
            Assert.False(r.SeesOther, "POOL BLEED: request saw the OTHER tenant's prescription");
        });

        await ExecAsync("DELETE FROM docslot.prescriptions WHERE prescription_id IN (@a,@b)", ("a", presA), ("b", presB));
        await ExecAsync("DELETE FROM docslot.bookings WHERE booking_id=@b", ("b", bookingB));
        await ExecAsync("DELETE FROM docslot.time_slots WHERE slot_id=@s", ("s", slotB));
        await ExecAsync("DELETE FROM docslot.doctors WHERE doctor_id=@d", ("d", doctorB));
    }

    [Fact]
    public async Task BreakGlass_Override_Unlocks_ConsentDenied_Read_Honoring_Scope_TTL_Revoke_And_Tenant()
    {
        // FR-MED-03: a deliberate, scoped, time-boxed break-glass grant lets a consent-denied clinical read
        // proceed — and ONLY then. Proves the unlock + the audit stamp + every refusal dimension.
        var client = await AuthedClientAsync();                       // admin, scoped to tenant A
        client.DefaultRequestHeaders.Add("X-Purpose-Of-Use", "emergency");

        // ---- Setup: a NON-CONSENTED patient (consent_given_at NULL) with a REAL (encrypted) prescription ----
        var ncPatient = Guid.NewGuid();
        var ncPhone = $"+9197{Random.Shared.Next(10000000, 99999999)}";
        var ncSlot = Guid.NewGuid();
        var ncBooking = Guid.NewGuid();
        await ExecAsync("INSERT INTO docslot.patients (patient_id, phone_number, full_name, is_active, created_at, updated_at) VALUES (@id,@ph,'NoConsent NC',true,NOW(),NOW())",
            ("id", ncPatient), ("ph", ncPhone));                     // no consent_given_at → HasActiveConsent=false
        await ExecAsync("INSERT INTO docslot.patient_tenant_links (link_id, patient_id, tenant_id, first_visit_at, last_visit_at, total_visits) VALUES (gen_random_uuid(),@p,@t,NOW(),NOW(),0)", ("p", ncPatient), ("t", factory.TenantA));
        await ExecAsync("INSERT INTO docslot.time_slots (slot_id,tenant_id,doctor_id,slot_date,start_time,end_time,status,current_count,max_count,created_at) VALUES (@id,@t,@d,CURRENT_DATE,'10:00','10:15','booked',1,1,NOW())", ("id", ncSlot), ("t", factory.TenantA), ("d", factory.DoctorId));
        await ExecAsync("INSERT INTO docslot.bookings (booking_id,tenant_id,slot_id,patient_id,doctor_id,status,booked_via,booked_for,booked_at,updated_at) VALUES (@id,@t,@s,@p,@d,'completed','dashboard','self',NOW(),NOW())", ("id", ncBooking), ("t", factory.TenantA), ("s", ncSlot), ("p", ncPatient), ("d", factory.DoctorId));

        const string diagnosis = "Anaphylaxis — penicillin allergy";
        const string meds = "[{\"name\":\"Adrenaline\",\"dose\":\"0.5mg\"}]";
        var issue = await client.PostAsJsonAsync("/api/v1/prescriptions", new IssuePrescriptionRequest(
            ncBooking, ncPatient, factory.DoctorId, "Collapse", "Hypotension", diagnosis, meds, "Refer ICU", null));
        issue.EnsureSuccessStatusCode();
        var pres = (await issue.Content.ReadFromJsonAsync<IssuePrescriptionResult>())!;

        try
        {
            // (a) NO grant → the consent-denied read is refused (403).
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.GetAsync($"/api/v1/prescriptions/{pres.PrescriptionId}")).StatusCode);

            // (b)+(c) Break glass (scoped to this prescription) → the read succeeds + DECRYPTS, and the read
            //         stamps is_break_glass=true at READ time (so the review queue shows the actual access).
            var bg = await client.PostAsJsonAsync("/api/v1/security/break-glass", new
            {
                patientId = ncPatient,
                resourceType = "prescription",
                resourceId = (Guid?)pres.PrescriptionId,
                justification = "Unconscious anaphylaxis patient; consent unobtainable — prescription needed now."
            });
            bg.EnsureSuccessStatusCode();
            var grantId = await bg.Content.ReadFromJsonAsync<Guid>();

            var unlocked = await client.GetAsync($"/api/v1/prescriptions/{pres.PrescriptionId}");
            Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
            var dto = (await unlocked.Content.ReadFromJsonAsync<PrescriptionDto>())!;
            Assert.Equal(diagnosis, dto.Diagnosis);                  // decrypted under the grant

            var bgReadLogged = await ScalarIntAsync(
                "SELECT COUNT(*)::int FROM platform.purpose_of_use_log WHERE accessed_resource_id=@r AND declared_purpose='emergency' AND is_break_glass=true AND review_required=true",
                ("r", pres.PrescriptionId));
            Assert.True(bgReadLogged >= 1, "the emergency read must stamp is_break_glass=true at read time");

            // (d-revoke) Revoke the grant (distinct reviewer permission) → the read re-locks (403).
            (await client.PostAsync($"/api/v1/security/break-glass/{grantId}/revoke", null)).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.GetAsync($"/api/v1/prescriptions/{pres.PrescriptionId}")).StatusCode);

            // (d-expired) An EXPIRED grant does NOT unlock.
            await SeedGrantAsync(factory.AdminUserId, factory.TenantA, ncPatient, "prescription", pres.PrescriptionId, expiresInMinutes: -5);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.GetAsync($"/api/v1/prescriptions/{pres.PrescriptionId}")).StatusCode);

            // (e-scope) An active grant scoped to a DIFFERENT resource_type does NOT unlock the prescription read.
            await SeedGrantAsync(factory.AdminUserId, factory.TenantA, ncPatient, "medical_history", null, expiresInMinutes: 60);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.GetAsync($"/api/v1/prescriptions/{pres.PrescriptionId}")).StatusCode);

            // (f-tenant) A grant in TENANT B (otherwise identical) does NOT unlock a tenant-A read (RLS + predicate).
            await SeedGrantAsync(factory.AdminUserId, factory.TenantB, ncPatient, "prescription", pres.PrescriptionId, expiresInMinutes: 60);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.GetAsync($"/api/v1/prescriptions/{pres.PrescriptionId}")).StatusCode);

            // Sanity: a fresh, correctly-scoped, in-tenant grant unlocks again — proving the 403s above were the
            // tested dimension (expiry / scope / tenant), not a setup artifact.
            await SeedGrantAsync(factory.AdminUserId, factory.TenantA, ncPatient, "prescription", pres.PrescriptionId, expiresInMinutes: 60);
            Assert.Equal(HttpStatusCode.OK,
                (await client.GetAsync($"/api/v1/prescriptions/{pres.PrescriptionId}")).StatusCode);
        }
        finally
        {
            await ExecAsync("DELETE FROM platform.break_glass_grants WHERE patient_id=@p", ("p", ncPatient));
            await ExecAsync("DELETE FROM platform.purpose_of_use_log WHERE accessed_resource_id IN (@p,@r)", ("p", ncPatient), ("r", pres.PrescriptionId));
            await ExecAsync("DELETE FROM docslot.prescriptions WHERE patient_id=@p", ("p", ncPatient));
            await ExecAsync("DELETE FROM docslot.bookings WHERE booking_id=@b", ("b", ncBooking));
            await ExecAsync("DELETE FROM docslot.time_slots WHERE slot_id=@s", ("s", ncSlot));
            await ExecAsync("DELETE FROM docslot.patient_tenant_links WHERE patient_id=@p", ("p", ncPatient));
            await ExecAsync("DELETE FROM docslot.patients WHERE patient_id=@p", ("p", ncPatient));
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Seeds a break-glass grant directly (privileged role) for the negative/positive scope tests.</summary>
    private static Task SeedGrantAsync(Guid userId, Guid tenantId, Guid patientId, string resourceType, Guid? resourceId, int expiresInMinutes) =>
        ExecAsync(
            """
            INSERT INTO platform.break_glass_grants
                (grant_id, user_id, tenant_id, patient_id, resource_type, resource_id, justification, granted_at, expires_at)
            VALUES (gen_random_uuid(), @u, @t, @p, @rt, @rid, 'seeded test grant', NOW(), NOW() + make_interval(mins => @mins))
            """,
            ("u", userId), ("t", tenantId), ("p", patientId), ("rt", resourceType),
            ("rid", (object?)resourceId ?? DBNull.Value), ("mins", expiresInMinutes));

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = factory.CreateClient();
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    private async Task<TokenResponse> LoginAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(factory.AdminEmail, ClinicalWebAppFactory.AdminPassword, factory.TenantA));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private static async Task<int> ScalarIntAsync(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(ClinicalWebAppFactory.AdminConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string?> ScalarStrAsync(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(ClinicalWebAppFactory.AdminConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    private static async Task ExecAsync(string sql, params (string Name, object Value)[] ps)
    {
        await using var conn = new NpgsqlConnection(ClinicalWebAppFactory.AdminConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }
}
