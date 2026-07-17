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
    // ---- Write-side tenant-ownership guards ------------------------------------------------------
    // docslot.doctors / docslot.test_catalog are tenant-scoped but have NO RLS, and prescriptions.doctor_id /
    // lab_reports.test_id are tenant-blind FKs — so the tenant predicate must be explicit here to reject a
    // cross-tenant reference at write (belt-and-suspenders behind the read-side JOIN predicates). No RLS on
    // these tables means app.tenant_id is irrelevant; the WHERE tenant_id does the scoping.

    // Also filters is_active / (doctors) deleted_at: a new clinical row must not reference a soft-deleted or
    // deactivated doctor/test even within the same tenant. test_catalog has no deleted_at column (is_active only).
    public async Task<bool> DoctorBelongsToTenantAsync(Guid doctorId, Guid tenantId, CancellationToken ct)
        => await ExistsAsync("SELECT EXISTS(SELECT 1 FROM docslot.doctors WHERE doctor_id = @p0 AND tenant_id = @p1 AND deleted_at IS NULL AND is_active)", doctorId, tenantId, ct);

    public async Task<bool> TestBelongsToTenantAsync(Guid testId, Guid tenantId, CancellationToken ct)
        => await ExistsAsync("SELECT EXISTS(SELECT 1 FROM docslot.test_catalog WHERE test_id = @p0 AND tenant_id = @p1 AND is_active)", testId, tenantId, ct);

    private async Task<bool> ExistsAsync(string sql, Guid id, Guid tenantId, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        cmd.Parameters.AddWithValue("@p0", id);
        cmd.Parameters.AddWithValue("@p1", tenantId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ---- Prescriptions ---------------------------------------------------------------------------

    public async Task<string?> AddPrescriptionAsync(Prescription p, CancellationToken ct)
    {
        // medications is jsonb → wrap the envelope string as a json string with to_jsonb; vitals (object) and
        // investigations (array) are plaintext JSONB cast from their raw JSON text. The BEFORE-INSERT trigger
        // assigns prescription_number (drafts included).
        var rows = await db.Database.SqlQueryRaw<NumberRow>(
                """
                INSERT INTO docslot.prescriptions
                    (prescription_id, booking_id, patient_id, doctor_id, tenant_id,
                     chief_complaints, examination, diagnosis, medications, advice, follow_up_in_days, status,
                     supersedes_prescription_id, amendment_reason,
                     vitals, investigations, drafted_by_user_id, finalized_by_user_id, finalized_at,
                     created_at, updated_at)
                VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, to_jsonb(@p8::text), @p9, @p10, @p11, @p13, @p14,
                        @p15::jsonb, @p16::jsonb, @p17, @p18, @p19, @p12, @p12)
                RETURNING prescription_number AS "Number"
                """,
                Params(
                    ("@p0", p.PrescriptionId), ("@p1", p.BookingId), ("@p2", p.PatientId), ("@p3", p.DoctorId), ("@p4", p.TenantId),
                    ("@p5", (object?)p.ChiefComplaintsEnc ?? DBNull.Value), ("@p6", (object?)p.ExaminationEnc ?? DBNull.Value),
                    ("@p7", (object?)p.DiagnosisEnc ?? DBNull.Value), ("@p8", p.MedicationsEnc),
                    ("@p9", (object?)p.Advice ?? DBNull.Value), ("@p10", (object?)p.FollowUpInDays ?? DBNull.Value),
                    ("@p11", p.Status), ("@p12", p.CreatedAt),
                    ("@p13", (object?)p.SupersedesPrescriptionId ?? DBNull.Value),
                    ("@p14", (object?)p.AmendmentReason ?? DBNull.Value),
                    ("@p15", p.Vitals), ("@p16", p.Investigations),
                    ("@p17", (object?)p.DraftedByUserId ?? DBNull.Value),
                    ("@p18", (object?)p.FinalizedByUserId ?? DBNull.Value),
                    ("@p19", (object?)p.FinalizedAt ?? DBNull.Value)))
            .ToListAsync(ct);
        return rows.FirstOrDefault()?.Number;
    }

    public Task<PrescriptionDetail?> GetPrescriptionAsync(Guid prescriptionId, Guid tenantId, CancellationToken ct)
        => ReadPrescriptionAsync("p.prescription_id = @p0 AND p.tenant_id = @p1", ct, ("@p0", prescriptionId), ("@p1", tenantId));

    public Task<PrescriptionDetail?> GetDraftByBookingAsync(Guid bookingId, Guid tenantId, CancellationToken ct)
        => ReadPrescriptionAsync("p.booking_id = @p0 AND p.tenant_id = @p1 AND p.status = 'draft'", ct, ("@p0", bookingId), ("@p1", tenantId));

    private async Task<PrescriptionDetail?> ReadPrescriptionAsync(string where, CancellationToken ct, params (string Name, object Value)[] ps)
    {
        // Read via a direct reader (unambiguous for jsonb #>> / ::text columns; avoids EF record-mapping pitfalls).
        // LEFT JOIN docslot.doctors for the (plaintext, non-PHI) doctor name; docslot.doctors is not RLS-enabled.
        // vitals (object) / investigations (array) read as ::text (their whole-object JSON); medications is a
        // json string envelope read via #>> '{}'.
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $$"""
            SELECT p.prescription_id, p.prescription_number, p.booking_id, p.patient_id, p.doctor_id, p.tenant_id,
                   p.chief_complaints, p.examination, p.diagnosis, p.medications #>> '{}', p.advice, p.follow_up_in_days,
                   p.status, p.supersedes_prescription_id, p.amendment_reason, p.created_at,
                   p.vitals::text, p.investigations::text, p.drafted_by_user_id, p.finalized_by_user_id, p.finalized_at,
                   d.full_name
            FROM docslot.prescriptions p
            LEFT JOIN docslot.doctors d ON d.doctor_id = p.doctor_id AND d.tenant_id = p.tenant_id
            WHERE {{where}}
            """, conn);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        // Enlist the ambient scope (read-scope query tx OR the command's UoW tx) so this raw
        // reader runs in the request's transaction (house pattern; mirrors GetMedicalHistoryAsync).
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        var prescription = Prescription.FromRow(
            rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.GetGuid(2), rd.GetGuid(3), rd.GetGuid(4), rd.GetGuid(5),
            rd.IsDBNull(6) ? null : rd.GetString(6), rd.IsDBNull(7) ? null : rd.GetString(7), rd.IsDBNull(8) ? null : rd.GetString(8),
            rd.GetString(9), rd.IsDBNull(10) ? null : rd.GetString(10), rd.IsDBNull(11) ? null : rd.GetInt32(11),
            rd.GetString(12), rd.IsDBNull(13) ? (Guid?)null : rd.GetGuid(13), rd.IsDBNull(14) ? null : rd.GetString(14),
            rd.GetDateTime(15),
            rd.IsDBNull(16) ? "{}" : rd.GetString(16), rd.IsDBNull(17) ? "[]" : rd.GetString(17),
            rd.IsDBNull(18) ? (Guid?)null : rd.GetGuid(18), rd.IsDBNull(19) ? (Guid?)null : rd.GetGuid(19),
            rd.IsDBNull(20) ? (DateTime?)null : rd.GetDateTime(20));
        var doctorName = rd.IsDBNull(21) ? null : rd.GetString(21);
        return new PrescriptionDetail(prescription, doctorName);
    }

    public async Task<Guid?> GetDoctorByUserIdAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        // docslot.doctors has no RLS + user_id/tenant_id scoping is explicit here (mirrors DoctorBelongsToTenantAsync).
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT doctor_id FROM docslot.doctors WHERE user_id = @p0 AND tenant_id = @p1 AND deleted_at IS NULL AND is_active LIMIT 1", conn);
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        cmd.Parameters.AddWithValue("@p0", userId);
        cmd.Parameters.AddWithValue("@p1", tenantId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is Guid g ? g : (Guid?)null;
    }

    public async Task<bool> UpdateDraftAsync(
        Guid prescriptionId, Guid tenantId, string? chiefComplaintsEnc, string? examinationEnc,
        string? diagnosisEnc, string? medicationsEnc, string? vitalsJson, string? investigationsJson,
        string? advice, int? followUpInDays, DateTime nowUtc, CancellationToken ct)
    {
        // Conditional UPDATE pinned to (prescription_id, tenant_id, status='draft'). COALESCE per field so a
        // null argument (field not sent) leaves the stored value untouched — a partial autosave never wipes.
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE docslot.prescriptions
               SET chief_complaints  = COALESCE(@p2::text, chief_complaints),
                   examination       = COALESCE(@p3::text, examination),
                   diagnosis         = COALESCE(@p4::text, diagnosis),
                   medications       = CASE WHEN @p5::text IS NULL THEN medications ELSE to_jsonb(@p5::text) END,
                   vitals            = COALESCE(@p6::jsonb, vitals),
                   investigations    = COALESCE(@p7::jsonb, investigations),
                   advice            = COALESCE(@p8::text, advice),
                   follow_up_in_days = COALESCE(@p9::int, follow_up_in_days),
                   updated_at        = @p10
             WHERE prescription_id = @p0 AND tenant_id = @p1 AND status = 'draft'
            """,
            new NpgsqlParameter("@p0", prescriptionId), new NpgsqlParameter("@p1", tenantId),
            new NpgsqlParameter("@p2", (object?)chiefComplaintsEnc ?? DBNull.Value),
            new NpgsqlParameter("@p3", (object?)examinationEnc ?? DBNull.Value),
            new NpgsqlParameter("@p4", (object?)diagnosisEnc ?? DBNull.Value),
            new NpgsqlParameter("@p5", (object?)medicationsEnc ?? DBNull.Value),
            new NpgsqlParameter("@p6", (object?)vitalsJson ?? DBNull.Value),
            new NpgsqlParameter("@p7", (object?)investigationsJson ?? DBNull.Value),
            new NpgsqlParameter("@p8", (object?)advice ?? DBNull.Value),
            new NpgsqlParameter("@p9", (object?)followUpInDays ?? DBNull.Value),
            new NpgsqlParameter("@p10", nowUtc));
        return affected > 0;
    }

    public async Task<bool> FinalizeAsync(
        Guid prescriptionId, Guid tenantId, Guid doctorId, Guid finalizedByUserId, DateTime finalizedAt, CancellationToken ct)
    {
        // Single-winner sign transition (only a draft can be signed). doctor_id is the server-derived prescriber
        // (never the draft's provisional value). Runs in the command's UoW tx.
        var rows = await db.Database.SqlQueryRaw<AmendedRow>(
                """
                UPDATE docslot.prescriptions
                SET status = 'finalized', doctor_id = @p2, finalized_by_user_id = @p3, finalized_at = @p4, updated_at = @p4
                WHERE prescription_id = @p0 AND tenant_id = @p1 AND status = 'draft'
                RETURNING status AS "Status"
                """,
                Params(("@p0", prescriptionId), ("@p1", tenantId), ("@p2", doctorId), ("@p3", finalizedByUserId), ("@p4", finalizedAt)))
            .ToListAsync(ct);
        return rows.Count > 0;
    }

    public async Task<IReadOnlyList<DrugAlert>> ListUnoverriddenAlertsAsync(Guid prescriptionId, Guid tenantId, CancellationToken ct)
    {
        var all = await ListDrugAlertsAsync(prescriptionId, tenantId, ct);
        return all.Where(a => !a.Overridden).ToList();
    }

    public async Task<int> MarkAlertsOverriddenAsync(
        Guid prescriptionId, Guid tenantId, Guid overriddenByUserId, string reason, DateTime nowUtc, CancellationToken ct)
    {
        // Mark the still-unoverridden high/critical (blocking) alerts overridden — never DELETE a row. Tenant scope
        // via the prescription EXISTS predicate (drug_alerts has no tenant_id); RLS enforces it too.
        return await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE docslot.drug_alerts da
               SET overridden = true, overridden_by_user_id = @p2, override_reason = @p3, overridden_at = @p4
             WHERE da.prescription_id = @p0 AND da.overridden = false AND da.severity IN ('high', 'critical')
               AND EXISTS (SELECT 1 FROM docslot.prescriptions p
                           WHERE p.prescription_id = da.prescription_id AND p.tenant_id = @p1)
            """,
            new NpgsqlParameter("@p0", prescriptionId), new NpgsqlParameter("@p1", tenantId),
            new NpgsqlParameter("@p2", overriddenByUserId), new NpgsqlParameter("@p3", reason),
            new NpgsqlParameter("@p4", nowUtc));
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
                SELECT p.prescription_id AS "PrescriptionId", p.prescription_number AS "PrescriptionNumber",
                       p.patient_id AS "PatientId", p.doctor_id AS "DoctorId", d.full_name AS "DoctorName",
                       p.status AS "Status", p.created_at AS "CreatedAt"
                FROM docslot.prescriptions p
                LEFT JOIN docslot.doctors d ON d.doctor_id = p.doctor_id AND d.tenant_id = p.tenant_id
                WHERE p.tenant_id = @p0 AND p.patient_id = @p1 ORDER BY p.created_at DESC
                """,
                Params(("@p0", tenantId), ("@p1", patientId)))
            .ToListAsync(ct);
        return rows.Select(r => new PrescriptionRow(r.PrescriptionId, r.PrescriptionNumber, r.PatientId, r.DoctorId, r.DoctorName, r.Status, r.CreatedAt)).ToList();
    }

    public async Task<IReadOnlyList<TimelinePrescriptionRow>> ListTimelinePrescriptionsAsync(Guid tenantId, Guid patientId, CancellationToken ct)
    {
        // NON-DRAFT rows only (a draft is not part of the record feed). diagnosis is a plain encrypted-text column;
        // medications is a jsonb-wrapped envelope read via #>> '{}'. LEFT JOIN docslot.doctors for the plaintext
        // prescriber name/specialization (directory data, NOT PHI; doctors has no RLS). Enlists the ambient read tx.
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT p.prescription_id, p.prescription_number, p.status, p.created_at, p.finalized_at,
                   p.diagnosis, p.medications #>> '{}', p.doctor_id, d.full_name, d.specialization
            FROM docslot.prescriptions p
            LEFT JOIN docslot.doctors d ON d.doctor_id = p.doctor_id AND d.tenant_id = p.tenant_id
            WHERE p.tenant_id = @p0 AND p.patient_id = @p1 AND p.status <> 'draft'
            ORDER BY COALESCE(p.finalized_at, p.created_at) DESC
            """, conn);
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        cmd.Parameters.AddWithValue("@p0", tenantId);
        cmd.Parameters.AddWithValue("@p1", patientId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<TimelinePrescriptionRow>();
        while (await rd.ReadAsync(ct))
            result.Add(new TimelinePrescriptionRow(
                rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.GetString(2), rd.GetDateTime(3),
                rd.IsDBNull(4) ? (DateTime?)null : rd.GetDateTime(4),
                rd.IsDBNull(5) ? null : rd.GetString(5), rd.GetString(6), rd.GetGuid(7),
                rd.IsDBNull(8) ? null : rd.GetString(8), rd.IsDBNull(9) ? null : rd.GetString(9)));
        return result;
    }

    public async Task<IReadOnlyList<TimelineLabRow>> ListTimelineLabReportsAsync(Guid tenantId, Guid patientId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<TimelineLabRowDb>(
                """
                SELECT r.report_id AS "ReportId", r.report_number AS "ReportNumber",
                       COALESCE(tc.test_name, 'Lab Test') AS "TestName", r.status AS "Status",
                       r.has_critical_findings AS "HasCriticalFindings", r.created_at AS "CreatedAt",
                       (r.file_url IS NOT NULL) AS "HasFile"
                FROM docslot.lab_reports r
                LEFT JOIN docslot.test_catalog tc ON tc.test_id = r.test_id AND tc.tenant_id = r.tenant_id
                WHERE r.tenant_id = @p0 AND r.patient_id = @p1
                ORDER BY r.created_at DESC
                """,
                Params(("@p0", tenantId), ("@p1", patientId)))
            .ToListAsync(ct);
        return rows.Select(r => new TimelineLabRow(r.ReportId, r.ReportNumber, r.TestName, r.Status, r.HasCriticalFindings, r.CreatedAt, r.HasFile)).ToList();
    }

    public async Task<PatientTimelineStrip> GetPatientTimelineStripAsync(Guid tenantId, Guid patientId, CancellationToken ct)
    {
        // Strip derived from bookings: first-ever booking date (Asia/Kolkata — the app's clinical timezone, so
        // "patient since" never shows the prior UTC day) + the count of NON-CANCELLED bookings. No bookings →
        // (null, 0). booked_at is timestamptz; the ::date is taken after the AT TIME ZONE conversion.
        var rows = await db.Database.SqlQueryRaw<StripRow>(
                """
                SELECT (MIN(booked_at) AT TIME ZONE 'Asia/Kolkata')::date AS "PatientSince",
                       COUNT(*) FILTER (WHERE status <> 'cancelled')::int AS "VisitCount"
                FROM docslot.bookings WHERE tenant_id = @p0 AND patient_id = @p1
                """,
                Params(("@p0", tenantId), ("@p1", patientId)))
            .ToListAsync(ct);
        var r = rows.FirstOrDefault();
        return r is null ? new PatientTimelineStrip(null, 0) : new PatientTimelineStrip(r.PatientSince, r.VisitCount);
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

    public async Task<LabReportDetail?> GetLabReportAsync(Guid reportId, Guid tenantId, CancellationToken ct)
    {
        // LEFT JOIN docslot.test_catalog for the (plaintext, non-PHI) test name — mirrors ListLabReportsAsync.
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT lr.report_id, lr.report_number, lr.booking_id, lr.patient_id, lr.tenant_id, lr.test_id, lr.file_name,
                   lr.file_url, lr.file_size_bytes, lr.file_mime_type,
                   lr.structured_results #>> '{}', lr.status, lr.has_critical_findings, lr.created_at,
                   COALESCE(tc.test_name, 'Lab Test') AS test_name
            FROM docslot.lab_reports lr
            LEFT JOIN docslot.test_catalog tc ON tc.test_id = lr.test_id AND tc.tenant_id = lr.tenant_id
            WHERE lr.report_id = @p0 AND lr.tenant_id = @p1
            """, conn);
        cmd.Parameters.AddWithValue("@p0", reportId);
        cmd.Parameters.AddWithValue("@p1", tenantId);
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;   // ambient tx (house pattern)
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        var report = LabReport.FromRow(
            rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.GetGuid(2), rd.GetGuid(3), rd.GetGuid(4),
            rd.IsDBNull(5) ? null : rd.GetGuid(5), rd.IsDBNull(6) ? null : rd.GetString(6),
            rd.IsDBNull(7) ? null : rd.GetString(7), rd.IsDBNull(8) ? (long?)null : rd.GetInt64(8),
            rd.IsDBNull(9) ? null : rd.GetString(9), rd.IsDBNull(10) ? null : rd.GetString(10),
            rd.GetString(11), rd.GetBoolean(12), rd.GetDateTime(13));
        var testName = rd.IsDBNull(14) ? null : rd.GetString(14);
        return new LabReportDetail(report, testName);
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
                LEFT JOIN docslot.test_catalog tc ON tc.test_id = r.test_id AND tc.tenant_id = r.tenant_id
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

    // Full projection incl. paper-import provenance/verification/attachment columns. Used by the patient-facing
    // read (list) + the get/verify path. ORDER matches ReadMedicalHistory below.
    private const string MedicalHistorySelect =
        """
        SELECT history_id, patient_id, tenant_id, record_type, title, description,
               severity, icd10_code, started_date, ended_date, is_active, is_critical, added_at,
               source, external_doctor_name, recorded_date, verified_by_user_id, verified_at, import_batch_id,
               attachment_url, attachment_file_name, attachment_mime_type, attachment_size_bytes
        """;

    private static MedicalHistory ReadMedicalHistory(NpgsqlDataReader rd) =>
        MedicalHistory.FromRow(
            rd.GetGuid(0), rd.GetGuid(1), rd.GetGuid(2), rd.GetString(3), rd.GetString(4),
            rd.IsDBNull(5) ? null : rd.GetString(5),
            rd.IsDBNull(6) ? null : rd.GetString(6), rd.IsDBNull(7) ? null : rd.GetString(7),
            rd.IsDBNull(8) ? null : rd.GetFieldValue<DateOnly>(8), rd.IsDBNull(9) ? null : rd.GetFieldValue<DateOnly>(9),
            rd.GetBoolean(10), rd.GetBoolean(11), rd.GetDateTime(12),
            rd.IsDBNull(13) ? null : rd.GetString(13), rd.IsDBNull(14) ? null : rd.GetString(14),
            rd.IsDBNull(15) ? null : rd.GetFieldValue<DateOnly>(15),
            rd.IsDBNull(16) ? (Guid?)null : rd.GetGuid(16), rd.IsDBNull(17) ? (DateTime?)null : rd.GetDateTime(17),
            rd.IsDBNull(18) ? (Guid?)null : rd.GetGuid(18),
            rd.IsDBNull(19) ? null : rd.GetString(19), rd.IsDBNull(20) ? null : rd.GetString(20),
            rd.IsDBNull(21) ? null : rd.GetString(21), rd.IsDBNull(22) ? (long?)null : rd.GetInt64(22));

    public async Task<IReadOnlyList<MedicalHistory>> ListMedicalHistoryAsync(Guid tenantId, Guid patientId, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            MedicalHistorySelect +
            " FROM docslot.patient_medical_history WHERE tenant_id = @p0 AND patient_id = @p1 AND is_active = true ORDER BY added_at DESC", conn);
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        cmd.Parameters.AddWithValue("@p0", tenantId);
        cmd.Parameters.AddWithValue("@p1", patientId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<MedicalHistory>();
        while (await rd.ReadAsync(ct))
            result.Add(ReadMedicalHistory(rd));
        return result;
    }

    public async Task<Guid> AddMedicalHistoryAsync(MedicalHistory h, CancellationToken ct)
    {
        // Runs in the command's tenant-scoped UoW tx (app.tenant_id set) → RLS WITH CHECK admits the in-tenant row.
        await db.Database.ExecuteSqlRawAsync(InsertMedicalHistorySql, InsertMedicalHistoryParams(h, "@p"));
        return h.HistoryId;
    }

    // Keeps each chunk's parameter count (24 cols/row) safely under Postgres's 65535-parameter-per-statement
    // limit, and keeps any single INSERT small even for an unusually large paper-import batch.
    private const int MedicalHistoryInsertChunkSize = 500;

    public async Task<IReadOnlyList<Guid>> AddMedicalHistoryBatchAsync(IReadOnlyList<MedicalHistory> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        // Chunked multi-row VALUES insert (mirrors AddDrugAlertsAsync below) instead of one round trip per row.
        // All chunks run inside the ONE command UoW tx → the whole batch still commits or rolls back atomically.
        // Every row already carries the shared import_batch_id + attachment pointer (set by the handler). RLS WITH
        // CHECK admits them (app.tenant_id set for the tx); each is UNVERIFIED (schema chk_history_verify_pair holds).
        foreach (var chunk in rows.Chunk(MedicalHistoryInsertChunkSize))
        {
            var values = new List<string>(chunk.Length);
            var ps = new List<NpgsqlParameter>(chunk.Length * 24);
            for (var i = 0; i < chunk.Length; i++)
            {
                var prefix = $"@p{i}_";
                values.Add('(' + string.Join(",", Enumerable.Range(0, 24).Select(j => $"{prefix}{j}")) + ')');
                ps.AddRange(InsertMedicalHistoryParams(chunk[i], prefix));
            }
            var sql = "INSERT INTO docslot.patient_medical_history " +
                      "(history_id, patient_id, tenant_id, record_type, title, description, severity, icd10_code, " +
                      "started_date, ended_date, is_active, is_critical, added_by_user_id, added_at, " +
                      "source, external_doctor_name, recorded_date, verified_by_user_id, verified_at, import_batch_id, " +
                      "attachment_url, attachment_file_name, attachment_mime_type, attachment_size_bytes) " +
                      "VALUES " + string.Join(",", values);
            await db.Database.ExecuteSqlRawAsync(sql, ps, ct);
        }
        return rows.Select(r => r.HistoryId).ToList();
    }

    // Shared INSERT (single-record create + batch import) — covers the paper-import columns. A clinic row carries
    // source='clinic' + verified_by/at (chk_history_clinic_rows_verified); an imported row is external + unverified.
    private const string InsertMedicalHistorySql =
        """
        INSERT INTO docslot.patient_medical_history
            (history_id, patient_id, tenant_id, record_type, title, description, severity, icd10_code,
             started_date, ended_date, is_active, is_critical, added_by_user_id, added_at,
             source, external_doctor_name, recorded_date, verified_by_user_id, verified_at, import_batch_id,
             attachment_url, attachment_file_name, attachment_mime_type, attachment_size_bytes)
        VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,
                @p14,@p15,@p16,@p17,@p18,@p19,@p20,@p21,@p22,@p23)
        """;

    private static NpgsqlParameter[] InsertMedicalHistoryParams(MedicalHistory h, string prefix) =>
    [
        new($"{prefix}0", h.HistoryId), new($"{prefix}1", h.PatientId), new($"{prefix}2", h.TenantId),
        new($"{prefix}3", h.RecordType), new($"{prefix}4", h.TitleEnc),
        new($"{prefix}5", (object?)h.DescriptionEnc ?? DBNull.Value),
        new($"{prefix}6", (object?)h.Severity ?? DBNull.Value), new($"{prefix}7", (object?)h.Icd10Code ?? DBNull.Value),
        new($"{prefix}8", (object?)h.StartedDate ?? DBNull.Value), new($"{prefix}9", (object?)h.EndedDate ?? DBNull.Value),
        new($"{prefix}10", h.IsActive), new($"{prefix}11", h.IsCritical),
        new($"{prefix}12", (object?)h.AddedByUserId ?? DBNull.Value), new($"{prefix}13", h.AddedAt),
        new($"{prefix}14", h.Source), new($"{prefix}15", (object?)h.ExternalDoctorNameEnc ?? DBNull.Value),
        new($"{prefix}16", (object?)h.RecordedDate ?? DBNull.Value),
        new($"{prefix}17", (object?)h.VerifiedByUserId ?? DBNull.Value), new($"{prefix}18", (object?)h.VerifiedAt ?? DBNull.Value),
        new($"{prefix}19", (object?)h.ImportBatchId ?? DBNull.Value),
        new($"{prefix}20", (object?)h.AttachmentUrl ?? DBNull.Value), new($"{prefix}21", (object?)h.AttachmentFileName ?? DBNull.Value),
        new($"{prefix}22", (object?)h.AttachmentMimeType ?? DBNull.Value), new($"{prefix}23", (object?)h.AttachmentSizeBytes ?? DBNull.Value),
    ];

    public async Task<MedicalHistory?> GetMedicalHistoryAsync(Guid historyId, Guid tenantId, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            MedicalHistorySelect + " FROM docslot.patient_medical_history WHERE history_id = @p0 AND tenant_id = @p1", conn);
        // Enlist the ambient tenant-scoped tx (this read runs inside the Update COMMAND's UoW tx) so app.tenant_id
        // is in scope for RLS — the explicit house pattern (AttributionRepository, break-glass GetActiveGrantAsync).
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        cmd.Parameters.AddWithValue("@p0", historyId);
        cmd.Parameters.AddWithValue("@p1", tenantId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return ReadMedicalHistory(rd);
    }

    public async Task<bool> VerifyMedicalHistoryAsync(Guid historyId, Guid tenantId, Guid verifiedByUserId, DateTime verifiedAt, CancellationToken ct)
    {
        // Single-winner conditional flip: stamps the verifier pair ONLY on an as-yet-unverified EXTERNAL row in
        // the tenant. A clinic row (already verified) or an already-verified import matches nothing → false, so a
        // concurrent double-verify writes exactly one audit row at the handler. Never re-stamps.
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE docslot.patient_medical_history
               SET verified_by_user_id = @p2, verified_at = @p3
             WHERE history_id = @p0 AND tenant_id = @p1 AND source <> 'clinic' AND verified_by_user_id IS NULL
            """,
            new NpgsqlParameter("@p0", historyId), new NpgsqlParameter("@p1", tenantId),
            new NpgsqlParameter("@p2", verifiedByUserId), new NpgsqlParameter("@p3", verifiedAt));
        return affected > 0;
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
    private sealed record ListRow(Guid PrescriptionId, string? PrescriptionNumber, Guid PatientId, Guid DoctorId, string? DoctorName, string Status, DateTime CreatedAt);
    private sealed record LabListRow(Guid ReportId, string? ReportNumber, string TestName, string Status, bool HasCriticalFindings, DateTime CreatedAt);
    private sealed record DeliverRow(string Status, DateTime? DeliveredAt);
    private sealed record AmendedRow(string Status);
    private sealed record LinkedRow(string? CareContextId);
    private sealed record ConsentRow(Guid PatientId, string? Phone, bool ClinicalConsentActive, bool AbdmConsentActive, DateTime? AbdmConsentExpiresAt);
    private sealed record StripRow(DateOnly? PatientSince, int VisitCount);
    private sealed record TimelineLabRowDb(Guid ReportId, string? ReportNumber, string TestName, string Status, bool HasCriticalFindings, DateTime CreatedAt, bool HasFile);
}
