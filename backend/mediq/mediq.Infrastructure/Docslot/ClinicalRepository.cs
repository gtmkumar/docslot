using mediq.Application.Abstractions;
using mediq.Domain.Docslot;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace mediq.Infrastructure.Docslot;

/// <summary>
/// Clinical PHI repository (RLS-protected; the app role is NOBYPASSRLS and the UoW sets app.tenant_id per
/// tx). Encrypted fields are stored/returned as ciphertext envelope strings. jsonb-encrypted columns
/// (medications/structured_results/fhir_bundle) store the envelope wrapped via <c>to_jsonb(text)</c> and are
/// read back via <c>#&gt;&gt;'{}'</c>. Inserts flush immediately so the DB trigger assigns PRX-/RPT- numbers.
/// </summary>
public sealed class ClinicalRepository(PlatformDbContext db) : IClinicalRepository
{
    // ---- Prescriptions ---------------------------------------------------------------------------

    public async Task<string?> AddPrescriptionAsync(Prescription p, CancellationToken ct)
    {
        // medications is jsonb → wrap the envelope string as a json string with to_jsonb.
        var rows = await db.Database.SqlQueryRaw<NumberRow>(
                """
                INSERT INTO docslot.prescriptions
                    (prescription_id, booking_id, patient_id, doctor_id, tenant_id,
                     chief_complaints, examination, diagnosis, medications, advice, follow_up_in_days, status,
                     supersedes_prescription_id, amendment_reason, created_at, updated_at)
                VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, to_jsonb(@p8::text), @p9, @p10, @p11, @p13, @p14, @p12, @p12)
                RETURNING prescription_number AS "Number"
                """,
                Params(
                    ("@p0", p.PrescriptionId), ("@p1", p.BookingId), ("@p2", p.PatientId), ("@p3", p.DoctorId), ("@p4", p.TenantId),
                    ("@p5", (object?)p.ChiefComplaintsEnc ?? DBNull.Value), ("@p6", (object?)p.ExaminationEnc ?? DBNull.Value),
                    ("@p7", (object?)p.DiagnosisEnc ?? DBNull.Value), ("@p8", p.MedicationsEnc),
                    ("@p9", (object?)p.Advice ?? DBNull.Value), ("@p10", (object?)p.FollowUpInDays ?? DBNull.Value),
                    ("@p11", p.Status), ("@p12", p.CreatedAt),
                    ("@p13", (object?)p.SupersedesPrescriptionId ?? DBNull.Value),
                    ("@p14", (object?)p.AmendmentReason ?? DBNull.Value)))
            .ToListAsync(ct);
        return rows.FirstOrDefault()?.Number;
    }

    public async Task<Prescription?> GetPrescriptionAsync(Guid prescriptionId, Guid tenantId, CancellationToken ct)
    {
        // Read via a direct reader (unambiguous for jsonb #>> text columns; avoids EF record-mapping pitfalls).
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT prescription_id, prescription_number, booking_id, patient_id, doctor_id, tenant_id,
                   chief_complaints, examination, diagnosis, medications #>> '{}', advice, follow_up_in_days,
                   status, supersedes_prescription_id, amendment_reason, created_at
            FROM docslot.prescriptions WHERE prescription_id = @p0 AND tenant_id = @p1
            """, conn);
        cmd.Parameters.AddWithValue("@p0", prescriptionId);
        cmd.Parameters.AddWithValue("@p1", tenantId);
        // Enlist the ambient scope (read-scope query tx OR the amend command's UoW tx) so this raw
        // reader runs in the request's transaction (house pattern; mirrors GetMedicalHistoryAsync).
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return Prescription.FromRow(
            rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.GetGuid(2), rd.GetGuid(3), rd.GetGuid(4), rd.GetGuid(5),
            rd.IsDBNull(6) ? null : rd.GetString(6), rd.IsDBNull(7) ? null : rd.GetString(7), rd.IsDBNull(8) ? null : rd.GetString(8),
            rd.GetString(9), rd.IsDBNull(10) ? null : rd.GetString(10), rd.IsDBNull(11) ? null : rd.GetInt32(11),
            rd.GetString(12), rd.IsDBNull(13) ? (Guid?)null : rd.GetGuid(13), rd.IsDBNull(14) ? null : rd.GetString(14),
            rd.GetDateTime(15));
    }

    public async Task<bool> MarkPrescriptionSupersededAsync(Guid prescriptionId, Guid tenantId, CancellationToken ct)
    {
        // Single-winner conditional flip: only an issued (finalized/delivered) prescription can be amended;
        // a draft or an already-'amended' row matches nothing → returns false. Runs in the command's UoW tx.
        var rows = await db.Database.SqlQueryRaw<AmendedRow>(
                """
                UPDATE docslot.prescriptions
                SET status = 'amended', updated_at = NOW()
                WHERE prescription_id = @p0 AND tenant_id = @p1 AND status IN ('finalized', 'delivered')
                RETURNING status AS "Status"
                """,
                Params(("@p0", prescriptionId), ("@p1", tenantId)))
            .ToListAsync(ct);
        return rows.Count > 0;
    }

    public async Task<IReadOnlyList<PrescriptionRow>> ListPrescriptionsAsync(Guid tenantId, Guid patientId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<ListRow>(
                """
                SELECT prescription_id AS "PrescriptionId", prescription_number AS "PrescriptionNumber",
                       patient_id AS "PatientId", doctor_id AS "DoctorId", status AS "Status", created_at AS "CreatedAt"
                FROM docslot.prescriptions WHERE tenant_id = @p0 AND patient_id = @p1 ORDER BY created_at DESC
                """,
                Params(("@p0", tenantId), ("@p1", patientId)))
            .ToListAsync(ct);
        return rows.Select(r => new PrescriptionRow(r.PrescriptionId, r.PrescriptionNumber, r.PatientId, r.DoctorId, r.Status, r.CreatedAt)).ToList();
    }

    // ---- Lab reports -----------------------------------------------------------------------------

    public async Task<string?> AddLabReportAsync(LabReport rpt, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<NumberRow>(
                """
                INSERT INTO docslot.lab_reports
                    (report_id, booking_id, patient_id, tenant_id, test_id, file_name, structured_results,
                     status, has_critical_findings, created_at, updated_at)
                VALUES (@p0, @p1, @p2, @p3, @p4, @p5, CASE WHEN @p6 IS NULL THEN NULL ELSE to_jsonb(@p6::text) END,
                        @p7, @p8, @p9, @p9)
                RETURNING report_number AS "Number"
                """,
                Params(
                    ("@p0", rpt.ReportId), ("@p1", rpt.BookingId), ("@p2", rpt.PatientId), ("@p3", rpt.TenantId),
                    ("@p4", (object?)rpt.TestId ?? DBNull.Value), ("@p5", (object?)rpt.FileName ?? DBNull.Value),
                    ("@p6", (object?)rpt.StructuredResultsEnc ?? DBNull.Value), ("@p7", rpt.Status),
                    ("@p8", rpt.HasCriticalFindings), ("@p9", rpt.CreatedAt)))
            .ToListAsync(ct);
        return rows.FirstOrDefault()?.Number;
    }

    public async Task<LabReport?> GetLabReportAsync(Guid reportId, Guid tenantId, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT report_id, report_number, booking_id, patient_id, tenant_id, test_id, file_name,
                   file_url, file_size_bytes, file_mime_type,
                   structured_results #>> '{}', status, has_critical_findings, created_at
            FROM docslot.lab_reports WHERE report_id = @p0 AND tenant_id = @p1
            """, conn);
        cmd.Parameters.AddWithValue("@p0", reportId);
        cmd.Parameters.AddWithValue("@p1", tenantId);
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;   // ambient tx (house pattern)
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return LabReport.FromRow(
            rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.GetGuid(2), rd.GetGuid(3), rd.GetGuid(4),
            rd.IsDBNull(5) ? null : rd.GetGuid(5), rd.IsDBNull(6) ? null : rd.GetString(6),
            rd.IsDBNull(7) ? null : rd.GetString(7), rd.IsDBNull(8) ? (long?)null : rd.GetInt64(8),
            rd.IsDBNull(9) ? null : rd.GetString(9), rd.IsDBNull(10) ? null : rd.GetString(10),
            rd.GetString(11), rd.GetBoolean(12), rd.GetDateTime(13));
    }

    public async Task<bool> SetLabReportFileAsync(
        Guid reportId, Guid tenantId, string storageKey, string fileName, long sizeBytes, string mimeType,
        Guid? uploadedByUserId, DateTime nowUtc, CancellationToken ct)
    {
        // Attach (or replace) the stored PHI artifact reference on a tenant-scoped report. pending → ready.
        var rows = await db.Database.SqlQueryRaw<AmendedRow>(
                """
                UPDATE docslot.lab_reports
                SET file_url = @p2, file_name = @p3, file_size_bytes = @p4, file_mime_type = @p5,
                    status = CASE WHEN status = 'pending' THEN 'ready' ELSE status END,
                    uploaded_at = @p7, uploaded_by_user_id = @p6, updated_at = @p7
                WHERE report_id = @p0 AND tenant_id = @p1
                RETURNING status AS "Status"
                """,
                Params(
                    ("@p0", reportId), ("@p1", tenantId), ("@p2", storageKey), ("@p3", fileName),
                    ("@p4", sizeBytes), ("@p5", mimeType),
                    ("@p6", (object?)uploadedByUserId ?? DBNull.Value), ("@p7", nowUtc)))
            .ToListAsync(ct);
        return rows.Count > 0;
    }

    public async Task<IReadOnlyList<LabReportListRow>> ListLabReportsAsync(Guid tenantId, Guid patientId, CancellationToken ct)
    {
        // Header rows only — structured_results (encrypted) is NEVER selected here (no clinical content in lists).
        var rows = await db.Database.SqlQueryRaw<LabListRow>(
                """
                SELECT r.report_id AS "ReportId", r.report_number AS "ReportNumber",
                       COALESCE(tc.test_name, 'Lab Test') AS "TestName", r.status AS "Status",
                       r.has_critical_findings AS "HasCriticalFindings", r.created_at AS "CreatedAt"
                FROM docslot.lab_reports r
                LEFT JOIN docslot.test_catalog tc ON tc.test_id = r.test_id
                WHERE r.tenant_id = @p0 AND r.patient_id = @p1
                ORDER BY r.created_at DESC
                """,
                Params(("@p0", tenantId), ("@p1", patientId)))
            .ToListAsync(ct);
        return rows.Select(r => new LabReportListRow(r.ReportId, r.ReportNumber, r.TestName, r.Status, r.HasCriticalFindings, r.CreatedAt)).ToList();
    }

    public async Task<(string Status, DateTime? DeliveredAt)?> DeliverLabReportAsync(Guid reportId, Guid tenantId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<DeliverRow>(
                """
                UPDATE docslot.lab_reports
                SET status = 'delivered', delivered_at = COALESCE(delivered_at, NOW()), updated_at = NOW()
                WHERE report_id = @p0 AND tenant_id = @p1
                RETURNING status AS "Status", delivered_at AS "DeliveredAt"
                """,
                Params(("@p0", reportId), ("@p1", tenantId)))
            .ToListAsync(ct);
        var r = rows.FirstOrDefault();
        return r is null ? null : (r.Status, r.DeliveredAt);
    }

    // ---- Medical history -------------------------------------------------------------------------

    public async Task<IReadOnlyList<MedicalHistory>> ListMedicalHistoryAsync(Guid tenantId, Guid patientId, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT history_id, patient_id, tenant_id, record_type, title, description,
                   severity, icd10_code, started_date, ended_date, is_active, is_critical, added_at
            FROM docslot.patient_medical_history WHERE tenant_id = @p0 AND patient_id = @p1 AND is_active = true
            ORDER BY added_at DESC
            """, conn);
        cmd.Parameters.AddWithValue("@p0", tenantId);
        cmd.Parameters.AddWithValue("@p1", patientId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<MedicalHistory>();
        while (await rd.ReadAsync(ct))
            result.Add(MedicalHistory.FromRow(
                rd.GetGuid(0), rd.GetGuid(1), rd.GetGuid(2), rd.GetString(3), rd.GetString(4),
                rd.IsDBNull(5) ? null : rd.GetString(5),
                rd.IsDBNull(6) ? null : rd.GetString(6), rd.IsDBNull(7) ? null : rd.GetString(7),
                rd.IsDBNull(8) ? null : rd.GetFieldValue<DateOnly>(8), rd.IsDBNull(9) ? null : rd.GetFieldValue<DateOnly>(9),
                rd.GetBoolean(10), rd.GetBoolean(11), rd.GetDateTime(12)));
        return result;
    }

    public async Task<Guid> AddMedicalHistoryAsync(MedicalHistory h, CancellationToken ct)
    {
        // Runs in the command's tenant-scoped UoW tx (app.tenant_id set) → RLS WITH CHECK admits the in-tenant row.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO docslot.patient_medical_history
                (history_id, patient_id, tenant_id, record_type, title, description, severity, icd10_code,
                 started_date, ended_date, is_active, is_critical, added_by_user_id, added_at)
            VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13)
            """,
            new NpgsqlParameter("@p0", h.HistoryId), new NpgsqlParameter("@p1", h.PatientId),
            new NpgsqlParameter("@p2", h.TenantId), new NpgsqlParameter("@p3", h.RecordType),
            new NpgsqlParameter("@p4", h.TitleEnc), new NpgsqlParameter("@p5", (object?)h.DescriptionEnc ?? DBNull.Value),
            new NpgsqlParameter("@p6", (object?)h.Severity ?? DBNull.Value), new NpgsqlParameter("@p7", (object?)h.Icd10Code ?? DBNull.Value),
            new NpgsqlParameter("@p8", (object?)h.StartedDate ?? DBNull.Value), new NpgsqlParameter("@p9", (object?)h.EndedDate ?? DBNull.Value),
            new NpgsqlParameter("@p10", h.IsActive), new NpgsqlParameter("@p11", h.IsCritical),
            new NpgsqlParameter("@p12", (object?)h.AddedByUserId ?? DBNull.Value), new NpgsqlParameter("@p13", h.AddedAt));
        return h.HistoryId;
    }

    public async Task<MedicalHistory?> GetMedicalHistoryAsync(Guid historyId, Guid tenantId, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT history_id, patient_id, tenant_id, record_type, title, description,
                   severity, icd10_code, started_date, ended_date, is_active, is_critical, added_at
            FROM docslot.patient_medical_history WHERE history_id = @p0 AND tenant_id = @p1
            """, conn);
        // Enlist the ambient tenant-scoped tx (this read runs inside the Update COMMAND's UoW tx) so app.tenant_id
        // is in scope for RLS — the explicit house pattern (AttributionRepository, break-glass GetActiveGrantAsync).
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        cmd.Parameters.AddWithValue("@p0", historyId);
        cmd.Parameters.AddWithValue("@p1", tenantId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return MedicalHistory.FromRow(
            rd.GetGuid(0), rd.GetGuid(1), rd.GetGuid(2), rd.GetString(3), rd.GetString(4),
            rd.IsDBNull(5) ? null : rd.GetString(5),
            rd.IsDBNull(6) ? null : rd.GetString(6), rd.IsDBNull(7) ? null : rd.GetString(7),
            rd.IsDBNull(8) ? null : rd.GetFieldValue<DateOnly>(8), rd.IsDBNull(9) ? null : rd.GetFieldValue<DateOnly>(9),
            rd.GetBoolean(10), rd.GetBoolean(11), rd.GetDateTime(12));
    }

    public async Task<bool> UpdateMedicalHistoryAsync(
        Guid historyId, Guid tenantId, string recordType, string titleEnc, string? descEnc,
        string? severity, string? icd10Code, DateOnly? startedDate, DateOnly? endedDate,
        bool isActive, bool isCritical, CancellationToken ct)
    {
        // Conditional UPDATE pinned to (history_id, tenant_id). No physical delete — is_active=false retires.
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE docslot.patient_medical_history
               SET record_type=@p2, title=@p3, description=@p4, severity=@p5, icd10_code=@p6,
                   started_date=@p7, ended_date=@p8, is_active=@p9, is_critical=@p10
             WHERE history_id=@p0 AND tenant_id=@p1
            """,
            new NpgsqlParameter("@p0", historyId), new NpgsqlParameter("@p1", tenantId),
            new NpgsqlParameter("@p2", recordType), new NpgsqlParameter("@p3", titleEnc),
            new NpgsqlParameter("@p4", (object?)descEnc ?? DBNull.Value), new NpgsqlParameter("@p5", (object?)severity ?? DBNull.Value),
            new NpgsqlParameter("@p6", (object?)icd10Code ?? DBNull.Value), new NpgsqlParameter("@p7", (object?)startedDate ?? DBNull.Value),
            new NpgsqlParameter("@p8", (object?)endedDate ?? DBNull.Value), new NpgsqlParameter("@p9", isActive),
            new NpgsqlParameter("@p10", isCritical));
        return affected == 1;
    }

    // ---- ABDM records ----------------------------------------------------------------------------

    public Task AddAbdmRecordAsync(AbdmHealthRecord rec, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO docslot.abdm_health_records
                (record_id, patient_id, tenant_id, booking_id, abha_number, record_type, fhir_bundle,
                 is_linked_to_phr, consent_id, created_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, to_jsonb(@p6::text), false, @p7, @p8)
            """,
            Params(
                ("@p0", rec.RecordId), ("@p1", rec.PatientId), ("@p2", rec.TenantId),
                ("@p3", (object?)rec.BookingId ?? DBNull.Value), ("@p4", rec.AbhaNumber), ("@p5", rec.RecordType),
                ("@p6", rec.FhirBundleEnc), ("@p7", (object?)rec.ConsentId ?? DBNull.Value), ("@p8", rec.CreatedAt)));

    public async Task<AbdmHealthRecord?> GetAbdmRecordAsync(Guid recordId, Guid tenantId, CancellationToken ct)
    {
        var list = await ReadAbdmAsync("WHERE record_id = @p0 AND tenant_id = @p1", ct, ("@p0", recordId), ("@p1", tenantId));
        return list.FirstOrDefault();
    }

    public async Task<IReadOnlyList<AbdmHealthRecord>> ListAbdmRecordsAsync(Guid tenantId, Guid patientId, CancellationToken ct) =>
        await ReadAbdmAsync("WHERE tenant_id = @p0 AND patient_id = @p1 ORDER BY created_at DESC", ct, ("@p0", tenantId), ("@p1", patientId));

    /// <summary>Single-winner conditional flip to LINKED (publishes the care context). Matches only an as-yet
    /// unlinked record in the tenant → a concurrent second link returns false (idempotent at the handler).
    /// Runs in the caller's tenant-scoped tx (the link command's settle phase).</summary>
    public async Task<bool> MarkAbdmRecordLinkedAsync(Guid recordId, Guid tenantId, string? careContextId, DateTime nowUtc, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<LinkedRow>(
                """
                UPDATE docslot.abdm_health_records
                SET is_linked_to_phr = true, linked_at = @p2, care_context_id = @p3
                WHERE record_id = @p0 AND tenant_id = @p1 AND is_linked_to_phr = false
                RETURNING care_context_id AS "CareContextId"
                """,
                Params(("@p0", recordId), ("@p1", tenantId), ("@p2", nowUtc), ("@p3", (object?)careContextId ?? DBNull.Value)))
            .ToListAsync(ct);
        return rows.Count > 0;
    }

    private async Task<List<AbdmHealthRecord>> ReadAbdmAsync(string where, CancellationToken ct, params (string Name, object Value)[] ps)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT record_id, patient_id, tenant_id, booking_id, abha_number, record_type, " +
            "fhir_bundle #>> '{}', is_linked_to_phr, consent_id, created_at, care_context_id, linked_at " +
            "FROM docslot.abdm_health_records " + where, conn);
        // Enlist the ambient tenant-scoped tx when reading inside a command (e.g. the link command's load phase);
        // null in a plain query path → runs on the connection scoped by the query behavior (house pattern).
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<AbdmHealthRecord>();
        while (await rd.ReadAsync(ct))
            result.Add(AbdmHealthRecord.FromRow(
                rd.GetGuid(0), rd.GetGuid(1), rd.GetGuid(2), rd.IsDBNull(3) ? null : rd.GetGuid(3),
                rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetBoolean(7),
                rd.IsDBNull(8) ? null : rd.GetString(8), rd.GetDateTime(9),
                rd.IsDBNull(10) ? null : rd.GetString(10), rd.IsDBNull(11) ? null : rd.GetDateTime(11)));
        return result;
    }

    // ---- Consent context -------------------------------------------------------------------------

    public async Task<ConsentContextRow?> GetConsentContextAsync(Guid tenantId, Guid patientId, CancellationToken ct)
    {
        // patients is cross-tenant (phone is global identity); abdm_consents is tenant-scoped. Clinical
        // consent = patients.consent_given_at present + active row. ABDM consent = an active granted, unexpired row.
        var rows = await db.Database.SqlQueryRaw<ConsentRow>(
                """
                SELECT p.patient_id AS "PatientId", p.phone_number AS "Phone",
                       (p.consent_given_at IS NOT NULL AND p.is_active AND p.deleted_at IS NULL) AS "ClinicalConsentActive",
                       (a.consent_id IS NOT NULL) AS "AbdmConsentActive",
                       a.expires_at AS "AbdmConsentExpiresAt"
                FROM docslot.patients p
                LEFT JOIN LATERAL (
                    SELECT consent_id, expires_at FROM docslot.abdm_consents
                    WHERE patient_id = p.patient_id AND requesting_tenant_id = @p1
                      AND status = 'granted' AND (expires_at IS NULL OR expires_at > NOW())
                    ORDER BY granted_at DESC NULLS LAST LIMIT 1
                ) a ON true
                WHERE p.patient_id = @p0
                """,
                Params(("@p0", patientId), ("@p1", tenantId)))
            .ToListAsync(ct);
        var r = rows.FirstOrDefault();
        if (r is null) return null;
        return new ConsentContextRow(r.PatientId, PhoneMasker.Mask(r.Phone), r.ClinicalConsentActive, r.AbdmConsentActive, r.AbdmConsentExpiresAt);
    }

    // ---- Drug-safety alerts ----------------------------------------------------------------------

    public async Task<IReadOnlyList<MedicalHistory>> ListSafetyHistoryAsync(Guid tenantId, Guid patientId, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT history_id, patient_id, tenant_id, record_type, title, description,
                   severity, icd10_code, started_date, ended_date, is_active, is_critical, added_at
            FROM docslot.patient_medical_history
            WHERE tenant_id = @p0 AND patient_id = @p1 AND is_active = true
              AND record_type IN ('allergy', 'medication')
            ORDER BY added_at DESC
            """, conn);
        // Runs inside the issue/amend COMMAND's UoW tx → enlist it so app.tenant_id is in scope for RLS (house pattern).
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        cmd.Parameters.AddWithValue("@p0", tenantId);
        cmd.Parameters.AddWithValue("@p1", patientId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<MedicalHistory>();
        while (await rd.ReadAsync(ct))
            result.Add(MedicalHistory.FromRow(
                rd.GetGuid(0), rd.GetGuid(1), rd.GetGuid(2), rd.GetString(3), rd.GetString(4),
                rd.IsDBNull(5) ? null : rd.GetString(5),
                rd.IsDBNull(6) ? null : rd.GetString(6), rd.IsDBNull(7) ? null : rd.GetString(7),
                rd.IsDBNull(8) ? null : rd.GetFieldValue<DateOnly>(8), rd.IsDBNull(9) ? null : rd.GetFieldValue<DateOnly>(9),
                rd.GetBoolean(10), rd.GetBoolean(11), rd.GetDateTime(12)));
        return result;
    }

    public async Task<int> AddDrugAlertsAsync(IReadOnlyList<DrugAlert> alerts, CancellationToken ct)
    {
        if (alerts.Count == 0) return 0;
        // Runs in the command's tenant-scoped UoW tx; the parent prescription is already inserted in the same tx,
        // so the drug_alerts RLS policy (EXISTS prescription with the current tenant) admits these rows.
        var values = new List<string>(alerts.Count);
        var ps = new List<NpgsqlParameter>(alerts.Count * 9);
        for (var i = 0; i < alerts.Count; i++)
        {
            var a = alerts[i];
            var b = i * 9;
            values.Add($"(@p{b},@p{b + 1},@p{b + 2},@p{b + 3},@p{b + 4},@p{b + 5},@p{b + 6},@p{b + 7},@p{b + 8})");
            ps.Add(new NpgsqlParameter($"@p{b}", a.AlertId));
            ps.Add(new NpgsqlParameter($"@p{b + 1}", a.PrescriptionId));
            ps.Add(new NpgsqlParameter($"@p{b + 2}", a.PatientId));
            ps.Add(new NpgsqlParameter($"@p{b + 3}", a.AlertType));
            ps.Add(new NpgsqlParameter($"@p{b + 4}", a.Severity));
            ps.Add(new NpgsqlParameter($"@p{b + 5}", a.MedicationName));
            ps.Add(new NpgsqlParameter($"@p{b + 6}", (object?)a.ConflictingRecordId ?? DBNull.Value));
            ps.Add(new NpgsqlParameter($"@p{b + 7}", a.Description));
            ps.Add(new NpgsqlParameter($"@p{b + 8}", a.CreatedAt));
        }
        var sql = "INSERT INTO docslot.drug_alerts " +
                  "(alert_id, prescription_id, patient_id, alert_type, severity, medication_name, conflicting_record_id, description, created_at) " +
                  "VALUES " + string.Join(",", values);
        return await db.Database.ExecuteSqlRawAsync(sql, ps, ct);
    }

    public async Task<IReadOnlyList<DrugAlert>> ListDrugAlertsAsync(Guid prescriptionId, Guid tenantId, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        // Tenant scope via the prescription join (drug_alerts has no tenant_id); RLS enforces it too.
        await using var cmd = new NpgsqlCommand(
            """
            SELECT da.alert_id, da.prescription_id, da.patient_id, da.alert_type, da.severity,
                   da.medication_name, da.conflicting_record_id, da.description, da.overridden, da.created_at
            FROM docslot.drug_alerts da
            WHERE da.prescription_id = @p0
              AND EXISTS (SELECT 1 FROM docslot.prescriptions p
                          WHERE p.prescription_id = da.prescription_id AND p.tenant_id = @p1)
            ORDER BY CASE da.severity WHEN 'critical' THEN 0 WHEN 'high' THEN 1 WHEN 'moderate' THEN 2 ELSE 3 END, da.created_at
            """, conn);
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        cmd.Parameters.AddWithValue("@p0", prescriptionId);
        cmd.Parameters.AddWithValue("@p1", tenantId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<DrugAlert>();
        while (await rd.ReadAsync(ct))
            result.Add(DrugAlert.FromRow(
                rd.GetGuid(0), rd.GetGuid(1), rd.GetGuid(2), rd.GetString(3), rd.GetString(4),
                rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetGuid(6), rd.GetString(7), rd.GetBoolean(8), rd.GetDateTime(9)));
        return result;
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static object[] Params(params (string Name, object Value)[] ps) =>
        ps.Select(p => (object)new NpgsqlParameter(p.Name, p.Value)).ToArray();

    private sealed record NumberRow(string? Number);
    private sealed record ListRow(Guid PrescriptionId, string? PrescriptionNumber, Guid PatientId, Guid DoctorId, string Status, DateTime CreatedAt);
    private sealed record LabListRow(Guid ReportId, string? ReportNumber, string TestName, string Status, bool HasCriticalFindings, DateTime CreatedAt);
    private sealed record DeliverRow(string Status, DateTime? DeliveredAt);
    private sealed record AmendedRow(string Status);
    private sealed record LinkedRow(string? CareContextId);
    private sealed record ConsentRow(Guid PatientId, string? Phone, bool ClinicalConsentActive, bool AbdmConsentActive, DateTime? AbdmConsentExpiresAt);
}
