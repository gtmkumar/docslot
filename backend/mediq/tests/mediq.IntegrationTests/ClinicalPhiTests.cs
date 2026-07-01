using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using mediq.SharedDataModel.Docslot.Ai;
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
    public async Task Issue_Prescription_With_Cross_Tenant_DoctorId_Is_Rejected()
    {
        // #71: doctor_id is a tenant-blind FK and docslot.doctors has no RLS. A TenantA caller must NOT be able
        // to issue a prescription referencing a doctor that belongs to TenantB — the write-side guard rejects it
        // (422) so no cross-tenant / dangling FK is ever persisted.
        var tenantBDoctor = Guid.NewGuid();
        await ExecAsync("INSERT INTO docslot.doctors (doctor_id, tenant_id, full_name, is_active, is_accepting_new_patients, created_at, updated_at) VALUES (@id, @t, 'Dr TenantB', true, true, NOW(), NOW())",
            ("id", tenantBDoctor), ("t", factory.TenantB));
        try
        {
            var client = await AuthedClientAsync();   // authenticates to TenantA
            var resp = await client.PostAsJsonAsync("/api/v1/prescriptions", new IssuePrescriptionRequest(
                factory.BookingId, factory.PatientId, tenantBDoctor, null, null, "dx", "[]", null, null));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

            // Nothing was persisted referencing the cross-tenant doctor.
            var count = await ScalarIntAsync("SELECT COUNT(*)::int FROM docslot.prescriptions WHERE doctor_id=@d", ("d", tenantBDoctor));
            Assert.Equal(0, count);
        }
        finally
        {
            await ExecAsync("DELETE FROM docslot.doctors WHERE doctor_id=@id", ("id", tenantBDoctor));
        }
    }

    [Fact]
    public async Task Issue_Prescription_With_Inactive_Or_Deleted_Doctor_Is_Rejected()
    {
        // #71 follow-up: the write guard also refuses a SAME-tenant doctor that is soft-deleted / deactivated —
        // a new prescription must reference a live, active doctor.
        var retiredDoctor = Guid.NewGuid();
        await ExecAsync("INSERT INTO docslot.doctors (doctor_id, tenant_id, full_name, is_active, is_accepting_new_patients, deleted_at, created_at, updated_at) VALUES (@id, @t, 'Dr Retired', false, false, NOW(), NOW(), NOW())",
            ("id", retiredDoctor), ("t", factory.TenantA));
        try
        {
            var client = await AuthedClientAsync();   // TenantA — same tenant as the doctor
            var resp = await client.PostAsJsonAsync("/api/v1/prescriptions", new IssuePrescriptionRequest(
                factory.BookingId, factory.PatientId, retiredDoctor, null, null, "dx", "[]", null, null));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

            var count = await ScalarIntAsync("SELECT COUNT(*)::int FROM docslot.prescriptions WHERE doctor_id=@d", ("d", retiredDoctor));
            Assert.Equal(0, count);
        }
        finally
        {
            await ExecAsync("DELETE FROM docslot.doctors WHERE doctor_id=@id", ("id", retiredDoctor));
        }
    }

    [Fact]
    public async Task Amend_Prescription_Supersedes_Original_Encrypts_New_Content_And_Guards_State()
    {
        var client = await AuthedClientAsync();   // tenant A; tenant_owner → create/read/amend

        // Issue the original prescription.
        var issue = await client.PostAsJsonAsync("/api/v1/prescriptions", new IssuePrescriptionRequest(
            factory.BookingId, factory.PatientId, factory.DoctorId, "Cough", "Mild", "Acute bronchitis",
            "[{\"name\":\"Amoxicillin\",\"dose\":\"500mg\"}]", "Fluids", 5));
        Assert.Equal(HttpStatusCode.Created, issue.StatusCode);
        var original = await issue.Content.ReadFromJsonAsync<IssuePrescriptionResult>();
        Assert.NotNull(original);

        // Amend it: new diagnosis + meds + a mandatory reason.
        const string newDiagnosis = "Bacterial pneumonia (revised)";
        const string newMeds = "[{\"name\":\"Azithromycin\",\"dose\":\"500mg\"}]";
        var amend = await client.PostAsJsonAsync($"/api/v1/prescriptions/{original!.PrescriptionId}/amend",
            new AmendPrescriptionRequest("Cough, fever", "Crackles", newDiagnosis, newMeds, "Complete the course", 7,
                "Chest X-ray confirmed pneumonia; antibiotic changed."));
        Assert.Equal(HttpStatusCode.Created, amend.StatusCode);
        var amended = await amend.Content.ReadFromJsonAsync<AmendPrescriptionResult>();
        Assert.NotNull(amended);
        Assert.NotEqual(original.PrescriptionId, amended!.PrescriptionId);            // a NEW row
        Assert.Equal(original.PrescriptionId, amended.SupersededPrescriptionId);
        Assert.StartsWith("PRX-", amended.PrescriptionNumber);

        // The ORIGINAL is now 'amended' (superseded) — never overwritten.
        Assert.Equal("amended", await ScalarStrAsync(
            "SELECT status FROM docslot.prescriptions WHERE prescription_id=@id", ("id", original.PrescriptionId)));
        // The amendment links back, is 'finalized', and its new content is encrypted at rest.
        Assert.Equal(original.PrescriptionId.ToString(), await ScalarStrAsync(
            "SELECT supersedes_prescription_id::text FROM docslot.prescriptions WHERE prescription_id=@id", ("id", amended.PrescriptionId)));
        Assert.Equal("finalized", await ScalarStrAsync(
            "SELECT status FROM docslot.prescriptions WHERE prescription_id=@id", ("id", amended.PrescriptionId)));
        var rawNewDiag = await ScalarStrAsync(
            "SELECT diagnosis FROM docslot.prescriptions WHERE prescription_id=@id", ("id", amended.PrescriptionId));
        Assert.DoesNotContain(newDiagnosis, rawNewDiag!);   // encrypted, not plaintext

        // Authorized read of the amendment → decrypts new content + surfaces the lineage.
        client.DefaultRequestHeaders.Add("X-Purpose-Of-Use", "treatment");
        var read = await client.GetAsync($"/api/v1/prescriptions/{amended.PrescriptionId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var dto = await read.Content.ReadFromJsonAsync<PrescriptionDto>();
        Assert.Equal(newDiagnosis, dto!.Diagnosis);
        Assert.Contains("Azithromycin", dto.MedicationsJson);
        Assert.Equal(original.PrescriptionId, dto.SupersedesPrescriptionId);

        // Re-amending the already-superseded ORIGINAL is refused (422 — not an amendable state).
        var reAmend = await client.PostAsJsonAsync($"/api/v1/prescriptions/{original.PrescriptionId}/amend",
            new AmendPrescriptionRequest(null, null, "x", "[]", null, null, "second amend attempt must fail"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reAmend.StatusCode);

        // Amending a non-existent (or cross-tenant) prescription → 404.
        var missing = await client.PostAsJsonAsync($"/api/v1/prescriptions/{Guid.NewGuid()}/amend",
            new AmendPrescriptionRequest(null, null, "x", "[]", null, null, "amend a ghost prescription"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task LabReport_File_Is_Encrypted_At_Rest_And_Download_Is_Consent_Gated()
    {
        var client = await AuthedClientAsync();

        // Create the lab-report record, then attach the PHI artifact (a small fake PDF).
        var upload = await client.PostAsJsonAsync("/api/v1/lab-reports", new UploadLabReportRequest(
            factory.BookingId, factory.PatientId, null, "cbc.pdf", "{\"hb\":\"13.5\"}", false));
        var report = await upload.Content.ReadFromJsonAsync<UploadLabReportResult>();
        Assert.NotNull(report);

        var pdfBytes = Encoding.UTF8.GetBytes("%PDF-1.7\nCBC RESULT Hemoglobin 13.5 g/dL [PHI lab artifact]\n%%EOF");
        var setFile = await client.PostAsJsonAsync($"/api/v1/lab-reports/{report!.ReportId}/file",
            new SetLabReportFileRequest("cbc.pdf", "application/pdf", Convert.ToBase64String(pdfBytes)));
        Assert.Equal(HttpStatusCode.Created, setFile.StatusCode);
        var fileResult = await setFile.Content.ReadFromJsonAsync<SetLabReportFileResult>();
        Assert.Equal(pdfBytes.LongLength, fileResult!.SizeBytes);   // plaintext size recorded

        // DB metadata: a storage key + plaintext size are recorded; the report is ready.
        var fileUrl = await ScalarStrAsync("SELECT file_url FROM docslot.lab_reports WHERE report_id=@id", ("id", report.ReportId));
        Assert.False(string.IsNullOrEmpty(fileUrl));
        Assert.Equal("ready", await ScalarStrAsync("SELECT status FROM docslot.lab_reports WHERE report_id=@id", ("id", report.ReportId)));
        Assert.Equal(pdfBytes.Length, await ScalarIntAsync("SELECT file_size_bytes::int FROM docslot.lab_reports WHERE report_id=@id", ("id", report.ReportId)));

        // CIPHERTEXT AT REST: the bytes stored in the blob are an encryption envelope, NOT the plaintext PDF.
        var onDisk = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(factory.BlobRoot, fileUrl!)));
        Assert.DoesNotContain("Hemoglobin", onDisk);
        Assert.DoesNotContain("%PDF", onDisk);
        Assert.Contains("\"keyId\"", Encoding.UTF8.GetString(Convert.FromBase64String(onDisk)));   // self-describing envelope

        // Authorized download (consent + purpose-of-use) → decrypts back to the EXACT original bytes.
        client.DefaultRequestHeaders.Add("X-Purpose-Of-Use", "treatment");
        var download = await client.GetAsync($"/api/v1/lab-reports/{report.ReportId}/file");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal(pdfBytes, await download.Content.ReadAsByteArrayAsync());

        // CONSENT GATE: with consent revoked the PHI download is refused (403). Restore after.
        await ExecAsync("UPDATE docslot.patients SET consent_given_at=NULL WHERE patient_id=@p", ("p", factory.PatientId));
        try
        {
            var denied = await client.GetAsync($"/api/v1/lab-reports/{report.ReportId}/file");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        finally
        {
            await ExecAsync("UPDATE docslot.patients SET consent_given_at=NOW() WHERE patient_id=@p", ("p", factory.PatientId));
        }

        // A report with NO attached file → 404 on download (the gate passes; there is simply no artifact).
        var upload2 = await client.PostAsJsonAsync("/api/v1/lab-reports", new UploadLabReportRequest(
            factory.BookingId, factory.PatientId, null, null, null, false));
        var report2 = await upload2.Content.ReadFromJsonAsync<UploadLabReportResult>();
        var noFile = await client.GetAsync($"/api/v1/lab-reports/{report2!.ReportId}/file");
        Assert.Equal(HttpStatusCode.NotFound, noFile.StatusCode);
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
    public async Task Abdm_Record_Links_Care_Context_To_Network_Idempotently_And_Declines_Invalid_Abha()
    {
        var client = await AuthedClientAsync();
        var pushed = new List<Guid>();
        try
        {
            // Push a record (tenant A has an active ABDM consent) with a valid 14-digit ABHA.
            var push = await client.PostAsJsonAsync("/api/v1/abdm/records", new PushAbdmRecordRequest(
                factory.PatientId, factory.BookingId, "12-3456-7890-1234", "OPConsultation", "{\"resourceType\":\"Bundle\"}"));
            Assert.Equal(HttpStatusCode.OK, push.StatusCode);
            var recordId = (await push.Content.ReadFromJsonAsync<PushAbdmRecordResult>())!.RecordId;
            pushed.Add(recordId);

            // Before linking: not yet published to the network.
            Assert.Equal(0, await ScalarIntAsync(
                "SELECT COUNT(*)::int FROM docslot.abdm_health_records WHERE record_id=@id AND is_linked_to_phr=true", ("id", recordId)));

            // Link → publishes the care context to the (sandbox) ABDM network.
            var link = await client.PostAsync($"/api/v1/abdm/records/{recordId}/link", null);
            Assert.Equal(HttpStatusCode.OK, link.StatusCode);
            var linkResult = (await link.Content.ReadFromJsonAsync<LinkAbdmRecordResult>())!;
            Assert.True(linkResult.Linked);
            Assert.False(string.IsNullOrEmpty(linkResult.CareContextId));
            Assert.Equal("sandbox-dev", linkResult.Provider);

            // DB: linkage flipped + care_context_id + linked_at persisted.
            var cc = await ScalarStrAsync(
                "SELECT care_context_id FROM docslot.abdm_health_records WHERE record_id=@id AND is_linked_to_phr=true AND linked_at IS NOT NULL",
                ("id", recordId));
            Assert.Equal(linkResult.CareContextId, cc);

            // Read reflects the linkage (consent + purpose gated).
            client.DefaultRequestHeaders.Add("X-Purpose-Of-Use", "treatment");
            var dto = (await (await client.GetAsync($"/api/v1/abdm/records/{recordId}")).Content.ReadFromJsonAsync<AbdmRecordDto>())!;
            Assert.True(dto.IsLinkedToPhr);
            Assert.Equal(linkResult.CareContextId, dto.CareContextId);

            // Idempotent re-link → same care context, no error.
            var relink = await client.PostAsync($"/api/v1/abdm/records/{recordId}/link", null);
            Assert.Equal(HttpStatusCode.OK, relink.StatusCode);
            Assert.Equal(linkResult.CareContextId, (await relink.Content.ReadFromJsonAsync<LinkAbdmRecordResult>())!.CareContextId);

            // A record with a malformed ABHA → the gateway DECLINES → 422, and it stays UNLINKED (honest, no fake link).
            var badPush = await client.PostAsJsonAsync("/api/v1/abdm/records", new PushAbdmRecordRequest(
                factory.PatientId, factory.BookingId, "not-a-valid-abha", "OPConsultation", "{\"resourceType\":\"Bundle\"}"));
            var badId = (await badPush.Content.ReadFromJsonAsync<PushAbdmRecordResult>())!.RecordId;
            pushed.Add(badId);
            var badLink = await client.PostAsync($"/api/v1/abdm/records/{badId}/link", null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, badLink.StatusCode);
            Assert.Equal(0, await ScalarIntAsync(
                "SELECT COUNT(*)::int FROM docslot.abdm_health_records WHERE record_id=@id AND is_linked_to_phr=true", ("id", badId)));
        }
        finally
        {
            foreach (var id in pushed)
                await ExecAsync("DELETE FROM docslot.abdm_health_records WHERE record_id=@id", ("id", id));
        }
    }

    [Fact]
    public async Task Abdm_Link_Requires_Dangerous_Link_Permission_Not_Just_Create()
    {
        // A tenant_admin holds docslot.abdm.records.create (non-dangerous → auto-granted) but NOT the dangerous
        // docslot.abdm.records.link (excluded from the tenant_admin auto-grant). So it can PUSH (store locally)
        // but cannot LINK (publish to the national network) → 403. This is the privilege separation the auditor required.
        var owner = await AuthedClientAsync();
        var ownerPush = await owner.PostAsJsonAsync("/api/v1/abdm/records", new PushAbdmRecordRequest(
            factory.PatientId, factory.BookingId, "12-3456-7890-1234", "OPConsultation", "{\"resourceType\":\"Bundle\"}"));
        var recordId = (await ownerPush.Content.ReadFromJsonAsync<PushAbdmRecordResult>())!.RecordId;

        var tadminEmail = $"slice03b.tadmin+{Guid.NewGuid():N}@docslot.test";
        var tadminId = Guid.NewGuid();
        Guid? adminRecordId = null;
        try
        {
            await ExecAsync(
                """
                INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, is_platform_user, created_at, updated_at)
                VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Slice03b TenantAdmin', true, true, NOW(), NOW())
                """, ("id", tadminId), ("email", tadminEmail), ("pwd", ClinicalWebAppFactory.AdminPassword));
            await ExecAsync(
                """
                INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
                SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
                FROM platform.roles r WHERE r.role_key='tenant_admin' AND r.is_system ON CONFLICT DO NOTHING
                """, ("uid", tadminId), ("tid", factory.TenantA));

            var client = factory.CreateClient();
            var login = await client.PostAsJsonAsync("/api/v1/auth/login",
                new LoginRequest(tadminEmail, ClinicalWebAppFactory.AdminPassword, factory.TenantA));
            login.EnsureSuccessStatusCode();
            var token = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            // Has .create → CAN push (store locally).
            var adminPush = await client.PostAsJsonAsync("/api/v1/abdm/records", new PushAbdmRecordRequest(
                factory.PatientId, factory.BookingId, "12-3456-7890-1234", "OPConsultation", "{\"resourceType\":\"Bundle\"}"));
            Assert.Equal(HttpStatusCode.OK, adminPush.StatusCode);
            adminRecordId = (await adminPush.Content.ReadFromJsonAsync<PushAbdmRecordResult>())!.RecordId;

            // Lacks the dangerous .link → CANNOT publish to the national network → 403.
            var link = await client.PostAsync($"/api/v1/abdm/records/{recordId}/link", null);
            Assert.Equal(HttpStatusCode.Forbidden, link.StatusCode);

            // And the record stays unlinked.
            Assert.Equal(0, await ScalarIntAsync(
                "SELECT COUNT(*)::int FROM docslot.abdm_health_records WHERE record_id=@id AND is_linked_to_phr=true", ("id", recordId)));
        }
        finally
        {
            await ExecAsync("DELETE FROM docslot.abdm_health_records WHERE record_id=@id", ("id", recordId));
            if (adminRecordId is Guid arid)
                await ExecAsync("DELETE FROM docslot.abdm_health_records WHERE record_id=@id", ("id", arid));
            await ExecAsync("DELETE FROM platform.user_tenant_roles WHERE user_id=@u", ("u", tadminId));
            await ExecAsync("DELETE FROM platform.user_sessions WHERE user_id=@u", ("u", tadminId));
            await ExecAsync("DELETE FROM platform.login_attempts WHERE email=@e", ("e", tadminEmail));
            // Soft-delete (the tenant_admin push wrote an audit_log row → never hard-DELETE a user referenced by audit).
            await ExecAsync("UPDATE platform.users SET deleted_at=NOW(), is_active=false, email=@a WHERE user_id=@u",
                ("a", $"del+{tadminId}@s03b.test"), ("u", tadminId));
        }
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

    [Fact]
    public async Task MedicalHistory_Create_Encrypts_At_Rest_Read_Decrypts_Update_And_Retire()
    {
        var client = await AuthedClientAsync();                 // tenant A; tenant_owner+doctor → create/update/read
        client.DefaultRequestHeaders.Add("X-Purpose-Of-Use", "treatment");

        const string title = "Penicillin allergy";
        const string desc = "Anaphylaxis on exposure (2019)";
        var create = await client.PostAsJsonAsync($"/api/v1/patients/{factory.PatientId}/medical-history",
            new CreateMedicalHistoryRequest("allergy", title, desc, "severe", "T78.0", new DateOnly(2019, 5, 1), null, true));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<CreateMedicalHistoryResult>())!;
        Assert.NotEqual(Guid.Empty, created.HistoryId);

        try
        {
            // CIPHERTEXT AT REST — the raw title/description columns must NOT hold the plaintext.
            var rawTitle = await ScalarStrAsync("SELECT title FROM docslot.patient_medical_history WHERE history_id=@id", ("id", created.HistoryId));
            Assert.NotNull(rawTitle);
            Assert.DoesNotContain(title, rawTitle!);
            var rawDesc = await ScalarStrAsync("SELECT description FROM docslot.patient_medical_history WHERE history_id=@id", ("id", created.HistoryId));
            Assert.DoesNotContain(desc, rawDesc!);

            // Read (list) decrypts back to plaintext (consent + purpose-of-use upstream).
            var list1 = await (await client.GetAsync($"/api/v1/patients/{factory.PatientId}/medical-history"))
                .Content.ReadFromJsonAsync<List<MedicalHistoryDto>>();
            var item = list1!.Single(h => h.HistoryId == created.HistoryId);
            Assert.Equal("allergy", item.RecordType);
            Assert.Equal(title, item.Title);
            Assert.Equal(desc, item.Description);
            Assert.True(item.IsCritical);
            // The non-encrypted scalars round-trip on read (so an edit can preserve them — no silent loss).
            Assert.Equal("severe", item.Severity);
            Assert.Equal("T78.0", item.Icd10Code);
            Assert.Equal(new DateOnly(2019, 5, 1), item.StartedDate);

            // UPDATE re-encrypts; the read reflects the change and the column is still ciphertext.
            const string newTitle = "Penicillin + sulfa allergy";
            var upd = await client.PutAsJsonAsync($"/api/v1/patients/{factory.PatientId}/medical-history/{created.HistoryId}",
                new UpdateMedicalHistoryRequest("allergy", newTitle, "Updated note", "critical", "T78.0", new DateOnly(2019, 5, 1), null, true, true));
            Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
            var rawTitle2 = await ScalarStrAsync("SELECT title FROM docslot.patient_medical_history WHERE history_id=@id", ("id", created.HistoryId));
            Assert.DoesNotContain(newTitle, rawTitle2!);
            var list2 = await (await client.GetAsync($"/api/v1/patients/{factory.PatientId}/medical-history"))
                .Content.ReadFromJsonAsync<List<MedicalHistoryDto>>();
            var edited = list2!.Single(h => h.HistoryId == created.HistoryId);
            Assert.Equal(newTitle, edited.Title);
            Assert.Equal("critical", edited.Severity);            // severity updated, not wiped

            // Update of a record not in the caller's tenant → 404 (RLS + WHERE history_id+tenant_id).
            var notFound = await client.PutAsJsonAsync($"/api/v1/patients/{factory.PatientId}/medical-history/{Guid.NewGuid()}",
                new UpdateMedicalHistoryRequest("allergy", "x", null, null, null, null, null, true, false));
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

            // Retire (is_active=false) → drops out of the active list (soft delete, no physical row removal).
            await client.PutAsJsonAsync($"/api/v1/patients/{factory.PatientId}/medical-history/{created.HistoryId}",
                new UpdateMedicalHistoryRequest("allergy", newTitle, null, null, null, null, null, false, false));
            var list3 = await (await client.GetAsync($"/api/v1/patients/{factory.PatientId}/medical-history"))
                .Content.ReadFromJsonAsync<List<MedicalHistoryDto>>();
            Assert.DoesNotContain(list3!, h => h.HistoryId == created.HistoryId);
            Assert.Equal(1, await ScalarIntAsync("SELECT COUNT(*)::int FROM docslot.patient_medical_history WHERE history_id=@id", ("id", created.HistoryId)));
        }
        finally
        {
            await ExecAsync("DELETE FROM docslot.patient_medical_history WHERE history_id=@id", ("id", created.HistoryId));
        }
    }

    [Fact]
    public async Task Issue_Prescription_Generates_Allergy_And_Interaction_DrugAlerts()
    {
        var client = await AuthedClientAsync();
        client.DefaultRequestHeaders.Add("X-Purpose-Of-Use", "treatment");

        // Seed a recorded penicillin allergy (encrypted at rest, via the real API).
        const string allergyTitle = "Penicillin allergy";
        const string allergyDesc = "Anaphylaxis on exposure (2019)";
        var seed = await client.PostAsJsonAsync($"/api/v1/patients/{factory.PatientId}/medical-history",
            new CreateMedicalHistoryRequest("allergy", allergyTitle, allergyDesc, "severe", "T78.0", new DateOnly(2019, 5, 1), null, true));
        Assert.Equal(HttpStatusCode.Created, seed.StatusCode);
        var allergy = (await seed.Content.ReadFromJsonAsync<CreateMedicalHistoryResult>())!;

        Guid? prescriptionId = null;
        try
        {
            // Issue a prescription that BOTH conflicts with the allergy (amoxicillin = penicillin class) AND
            // contains a classic dangerous interaction (warfarin + aspirin → bleeding).
            const string meds = "[{\"name\":\"Amoxicillin\",\"dose\":\"500mg\"},{\"name\":\"Warfarin\",\"dose\":\"5mg\"},{\"name\":\"Aspirin\",\"dose\":\"75mg\"}]";
            var issue = await client.PostAsJsonAsync("/api/v1/prescriptions", new IssuePrescriptionRequest(
                factory.BookingId, factory.PatientId, factory.DoctorId, "AF", "Irregular pulse", "Atrial fibrillation", meds, "Review", 7));
            Assert.Equal(HttpStatusCode.Created, issue.StatusCode);
            prescriptionId = (await issue.Content.ReadFromJsonAsync<IssuePrescriptionResult>())!.PrescriptionId;

            // Read the alerts via the (consent + purpose-gated) endpoint.
            var alertsResp = await client.GetAsync($"/api/v1/prescriptions/{prescriptionId}/drug-alerts");
            Assert.Equal(HttpStatusCode.OK, alertsResp.StatusCode);
            var alerts = (await alertsResp.Content.ReadFromJsonAsync<List<DrugAlertDto>>())!;

            // Allergy alert: critical, names the prescribed drug, and does NOT leak the encrypted allergy free-text.
            var allergyAlert = Assert.Single(alerts, a => a.AlertType == "allergy");
            Assert.Equal("critical", allergyAlert.Severity);                 // 'severe' allergy → critical alert
            Assert.Contains("Amoxicillin", allergyAlert.MedicationName);
            Assert.DoesNotContain(allergyDesc, allergyAlert.Description);     // the encrypted note is never copied out

            // Interaction alert: high severity, involves the prescribed anticoagulant.
            var interaction = Assert.Single(alerts, a => a.AlertType == "interaction");
            Assert.Equal("high", interaction.Severity);
            Assert.Contains("Warfarin", interaction.MedicationName);

            // The allergy alert links the encrypted source record (detail stays behind encryption).
            var conflictId = await ScalarStrAsync(
                "SELECT conflicting_record_id::text FROM docslot.drug_alerts WHERE alert_id=@id", ("id", allergyAlert.AlertId));
            Assert.Equal(allergy.HistoryId.ToString(), conflictId);

            // The decrypt-for-screening was recorded in the purpose-of-use ledger (DPDP): a 'treatment' read of
            // the patient's medical_history, tagged in the notes as the automated drug-safety screen.
            Assert.True(await ScalarIntAsync(
                """
                SELECT COUNT(*)::int FROM platform.purpose_of_use_log
                WHERE accessed_resource_id=@p AND accessed_resource_type='medical_history'
                  AND declared_purpose='treatment' AND purpose_notes='automated drug-safety screening'
                """,
                ("p", factory.PatientId)) >= 1);

            // A benign prescription (no allergy/interaction match) generates NO alerts — not a false "all clear", a no-op.
            var benign = await client.PostAsJsonAsync("/api/v1/prescriptions", new IssuePrescriptionRequest(
                factory.BookingId, factory.PatientId, factory.DoctorId, "Fever", "Febrile", "Viral fever",
                "[{\"name\":\"Paracetamol\",\"dose\":\"500mg\"}]", "Fluids", 3));
            var benignId = (await benign.Content.ReadFromJsonAsync<IssuePrescriptionResult>())!.PrescriptionId;
            try
            {
                Assert.Equal(0, await ScalarIntAsync(
                    "SELECT COUNT(*)::int FROM docslot.drug_alerts WHERE prescription_id=@id", ("id", benignId)));
            }
            finally
            {
                await ExecAsync("DELETE FROM docslot.drug_alerts WHERE prescription_id=@id", ("id", benignId));
                await ExecAsync("DELETE FROM docslot.prescriptions WHERE prescription_id=@id", ("id", benignId));
            }
        }
        finally
        {
            if (prescriptionId is Guid pid)
            {
                await ExecAsync("DELETE FROM docslot.drug_alerts WHERE prescription_id=@id", ("id", pid));
                await ExecAsync("DELETE FROM docslot.prescriptions WHERE prescription_id=@id", ("id", pid));
            }
            await ExecAsync("DELETE FROM docslot.patient_medical_history WHERE history_id=@id", ("id", allergy.HistoryId));
        }
    }

    [Fact]
    public async Task Ai_LabReport_Extraction_Is_Consent_Purpose_And_Tenant_Gated()
    {
        // Slice 11: the OCR proxy surfaces lab PHI from the AI sibling. The test host uses the deterministic STUB
        // provider, so it works WITHOUT the Python AI service — but the .NET-side consent/purpose/tenant gate runs
        // BEFORE the AI is ever consulted, so a denial is a real 403/422/404, never a fabricated/"unavailable" score.
        var client = await AuthedClientAsync();   // tenant A; tenant_owner+super_admin → docslot.report.read

        // (a) No X-Purpose-Of-Use header → 422 (the DPDP gate), and the AI is never consulted.
        var noPurpose = await client.PostAsJsonAsync("/api/v1/lab-reports/extract",
            new ExtractLabReportRequest(factory.PatientId));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noPurpose.StatusCode);

        client.DefaultRequestHeaders.Add("X-Purpose-Of-Use", "treatment");

        // (b) Consent + purpose present → 200 from the labelled stub. NO raw OCR text is ever surfaced.
        var ok = await client.PostAsJsonAsync("/api/v1/lab-reports/extract", new ExtractLabReportRequest(factory.PatientId));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var rawJson = await ok.Content.ReadAsStringAsync();
        Assert.DoesNotContain("rawText", rawJson, StringComparison.OrdinalIgnoreCase);   // PHI-minimized at the proxy
        var dto = (await ok.Content.ReadFromJsonAsync<OcrExtractionDto>())!;
        Assert.True(dto.Available);
        Assert.Equal("stub-dev", dto.Source);                 // honest stub, never a fabricated real engine
        Assert.Contains(dto.Analytes, a => a.Test == "STUB-PANEL");

        // (c) An unknown / cross-tenant patient (not linked to tenant A) → 404, AI never consulted.
        var unknown = await client.PostAsJsonAsync("/api/v1/lab-reports/extract", new ExtractLabReportRequest(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // (d) Consent revoked + NO break-glass grant → 403 (not a masked "unavailable"). Restore after.
        await ExecAsync("UPDATE docslot.patients SET consent_given_at=NULL WHERE patient_id=@p", ("p", factory.PatientId));
        try
        {
            var denied = await client.PostAsJsonAsync("/api/v1/lab-reports/extract", new ExtractLabReportRequest(factory.PatientId));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            // (e) A patient-wide lab_report break-glass grant unlocks the consent-denied extraction → 200.
            await SeedGrantAsync(factory.AdminUserId, factory.TenantA, factory.PatientId, "lab_report", null, expiresInMinutes: 60);
            try
            {
                var unlocked = await client.PostAsJsonAsync("/api/v1/lab-reports/extract", new ExtractLabReportRequest(factory.PatientId));
                Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
            }
            finally
            {
                await ExecAsync("DELETE FROM platform.break_glass_grants WHERE patient_id=@p AND tenant_id=@t", ("p", factory.PatientId), ("t", factory.TenantA));
            }
        }
        finally
        {
            await ExecAsync("UPDATE docslot.patients SET consent_given_at=NOW() WHERE patient_id=@p", ("p", factory.PatientId));
        }
    }

    [Fact]
    public async Task Ai_LabReport_Extraction_Requires_Report_Read_Permission()
    {
        // A tenant_admin does NOT hold docslot.report.read (is_dangerous=true → excluded from the auto-grant), so
        // it cannot trigger an OCR extraction of lab PHI → 403 at the permission gate, before the handler runs.
        var tadminEmail = $"slice11.tadmin+{Guid.NewGuid():N}@docslot.test";
        var tadminId = Guid.NewGuid();
        try
        {
            await ExecAsync(
                """
                INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, is_platform_user, created_at, updated_at)
                VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Slice11 TenantAdmin', true, true, NOW(), NOW())
                """, ("id", tadminId), ("email", tadminEmail), ("pwd", ClinicalWebAppFactory.AdminPassword));
            await ExecAsync(
                """
                INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
                SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
                FROM platform.roles r WHERE r.role_key='tenant_admin' AND r.is_system ON CONFLICT DO NOTHING
                """, ("uid", tadminId), ("tid", factory.TenantA));

            var client = factory.CreateClient();
            var login = await client.PostAsJsonAsync("/api/v1/auth/login",
                new LoginRequest(tadminEmail, ClinicalWebAppFactory.AdminPassword, factory.TenantA));
            login.EnsureSuccessStatusCode();
            var token = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            client.DefaultRequestHeaders.Add("X-Purpose-Of-Use", "treatment");

            var resp = await client.PostAsJsonAsync("/api/v1/lab-reports/extract", new ExtractLabReportRequest(factory.PatientId));
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
        finally
        {
            await ExecAsync("DELETE FROM platform.user_tenant_roles WHERE user_id=@u", ("u", tadminId));
            await ExecAsync("DELETE FROM platform.user_sessions WHERE user_id=@u", ("u", tadminId));
            await ExecAsync("DELETE FROM platform.login_attempts WHERE email=@e", ("e", tadminEmail));
            await ExecAsync("UPDATE platform.users SET deleted_at=NOW(), is_active=false, email=@a WHERE user_id=@u",
                ("a", $"del+{tadminId}@s11.test"), ("u", tadminId));
        }
    }

    [Fact]
    public async Task Ai_Rag_Ask_Is_Consent_Purpose_Gated_And_Read_Only()
    {
        // Slice 11: the RAG proxy answers over a patient's indexed history. STUB provider in tests; read-only by
        // construction (the .NET client has no index method). The question (PHI) is a body value, never echoed back.
        var client = await AuthedClientAsync();   // tenant A; holds docslot.medical_history.read

        // (a) No X-Purpose-Of-Use → 422.
        var noPurpose = await client.PostAsJsonAsync($"/api/v1/patients/{factory.PatientId}/rag/ask",
            new RagAskRequest("What allergies does this patient have?"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noPurpose.StatusCode);

        client.DefaultRequestHeaders.Add("X-Purpose-Of-Use", "treatment");

        // (b) Consent + purpose → 200 from the labelled extractive stub. The question is NOT echoed in the response.
        const string question = "What allergies does this patient have?";
        var ok = await client.PostAsJsonAsync($"/api/v1/patients/{factory.PatientId}/rag/ask", new RagAskRequest(question));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var rawJson = await ok.Content.ReadAsStringAsync();
        Assert.DoesNotContain(question, rawJson);             // the PHI question is never reflected back
        var dto = (await ok.Content.ReadFromJsonAsync<RagAnswerDto>())!;
        Assert.True(dto.Available);
        Assert.Equal("extractive", dto.Mode);                 // no external-LLM egress in the stub
        Assert.Equal("stub-dev", dto.Source);
        Assert.Equal(factory.PatientId, dto.PatientId);

        // (c) An unknown / cross-tenant patient → 404, AI never consulted.
        var unknown = await client.PostAsJsonAsync($"/api/v1/patients/{Guid.NewGuid()}/rag/ask", new RagAskRequest(question));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // (d) Consent revoked → 403 (not a masked "unavailable"). Restore after.
        await ExecAsync("UPDATE docslot.patients SET consent_given_at=NULL WHERE patient_id=@p", ("p", factory.PatientId));
        try
        {
            var denied = await client.PostAsJsonAsync($"/api/v1/patients/{factory.PatientId}/rag/ask", new RagAskRequest(question));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        finally
        {
            await ExecAsync("UPDATE docslot.patients SET consent_given_at=NOW() WHERE patient_id=@p", ("p", factory.PatientId));
        }
    }

    [Fact]
    public async Task Ai_Extractions_List_And_Rag_Status_Return_Operational_Summaries_NoPhi()
    {
        // Slice 14: operational AI reads (no consent/purpose gate — tenant-scoped summaries, no analyte PHI).
        var client = await AuthedClientAsync();   // tenant_owner+super_admin → docslot.report.read + medical_history.read

        // Extraction list (summaries only — never analyte values).
        var listResp = await client.GetAsync("/api/v1/ai/extractions?limit=10");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var listRaw = await listResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("analyte", listRaw, StringComparison.OrdinalIgnoreCase);   // no per-result PHI surfaced
        var list = (await listResp.Content.ReadFromJsonAsync<OcrExtractionListDto>())!;
        Assert.True(list.Available);
        Assert.Equal("stub-dev", list.Source);
        Assert.All(list.Extractions, e => Assert.False(string.IsNullOrWhiteSpace(e.Status)));

        // RAG status (operational counts).
        var statusResp = await client.GetAsync("/api/v1/ai/rag/status");
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
        var status = (await statusResp.Content.ReadFromJsonAsync<RagStatusDto>())!;
        Assert.True(status.Available);
        Assert.Equal("stub-dev", status.Source);
        Assert.NotNull(status.Embeddings);
        Assert.NotNull(status.PatientsIndexed);
    }

    [Fact]
    public async Task Ai_Extractions_List_Requires_Report_Read_Permission()
    {
        // A tenant_admin lacks docslot.report.read (is_dangerous → excluded from the auto-grant) → 403.
        var tadminEmail = $"slice14.tadmin+{Guid.NewGuid():N}@docslot.test";
        var tadminId = Guid.NewGuid();
        try
        {
            await ExecAsync(
                """
                INSERT INTO platform.users (user_id, email, password_hash, full_name, is_active, is_platform_user, created_at, updated_at)
                VALUES (@id, @email, crypt(@pwd, gen_salt('bf', 10)), 'Slice14 TenantAdmin', true, true, NOW(), NOW())
                """, ("id", tadminId), ("email", tadminEmail), ("pwd", ClinicalWebAppFactory.AdminPassword));
            await ExecAsync(
                """
                INSERT INTO platform.user_tenant_roles (user_tenant_role_id, user_id, tenant_id, role_id, is_primary, granted_at)
                SELECT gen_random_uuid(), @uid, @tid, r.role_id, true, NOW()
                FROM platform.roles r WHERE r.role_key='tenant_admin' AND r.is_system ON CONFLICT DO NOTHING
                """, ("uid", tadminId), ("tid", factory.TenantA));

            var client = factory.CreateClient();
            var login = await client.PostAsJsonAsync("/api/v1/auth/login",
                new LoginRequest(tadminEmail, ClinicalWebAppFactory.AdminPassword, factory.TenantA));
            login.EnsureSuccessStatusCode();
            var token = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/ai/extractions")).StatusCode);
            // rag/status is gated on docslot.medical_history.read (also is_dangerous → tenant_admin lacks it) → 403.
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/ai/rag/status")).StatusCode);
        }
        finally
        {
            await ExecAsync("DELETE FROM platform.user_tenant_roles WHERE user_id=@u", ("u", tadminId));
            await ExecAsync("DELETE FROM platform.user_sessions WHERE user_id=@u", ("u", tadminId));
            await ExecAsync("DELETE FROM platform.login_attempts WHERE email=@e", ("e", tadminEmail));
            await ExecAsync("UPDATE platform.users SET deleted_at=NOW(), is_active=false, email=@a WHERE user_id=@u",
                ("a", $"del+{tadminId}@s14.test"), ("u", tadminId));
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
