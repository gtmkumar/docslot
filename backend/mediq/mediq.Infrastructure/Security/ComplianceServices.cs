using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Security;

/// <summary>
/// DPDP §11 portability: assembles a patient's booking-core data into a FHIR-R4-shaped bundle (stub: a
/// structured JSON envelope with the rows we hold). Clinical PHI resources arrive with slice 03b/05 once
/// the encrypted clinical tables are served.
/// </summary>
public sealed class DataExportService(PlatformDbContext db) : IDataExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Guid> CreateRequestAsync(string subjectPhone, string format, Guid? requesterUserId, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform.data_export_requests (request_id, requester_user_id, subject_phone, export_format, status, created_at)
            VALUES (@p0, @p1, @p2, @p3, 'processing', NOW())
            """,
            new NpgsqlParameter("@p0", id),
            new NpgsqlParameter("@p1", (object?)requesterUserId ?? DBNull.Value),
            new NpgsqlParameter("@p2", subjectPhone),
            new NpgsqlParameter("@p3", format));
        return id;
    }

    public async Task<DataExportResult> AssembleAsync(Guid requestId, string subjectPhone, Guid byUserId, CancellationToken ct)
    {
        // Patient demographics + bookings (booking-core; no clinical PHI this slice).
        var patientRows = await db.Database.SqlQueryRaw<PatientRow>(
                """
                SELECT patient_id AS "PatientId", full_name AS "FullName", age AS "Age", gender AS "Gender",
                       preferred_language AS "Lang"
                FROM docslot.patients WHERE phone_number = @p0 AND deleted_at IS NULL
                """,
                new NpgsqlParameter("@p0", subjectPhone))
            .ToListAsync(ct);

        var bookingRows = await db.Database.SqlQueryRaw<BookingRow>(
                """
                SELECT b.booking_number AS "BookingNumber", b.status AS "Status", b.booked_at AS "BookedAt"
                FROM docslot.bookings b JOIN docslot.patients p ON p.patient_id = b.patient_id
                WHERE p.phone_number = @p0 ORDER BY b.booked_at DESC LIMIT 1000
                """,
                new NpgsqlParameter("@p0", subjectPhone))
            .ToListAsync(ct);

        // FHIR-R4-shaped Bundle stub (Patient + Appointment entries).
        var entries = new List<object>();
        foreach (var p in patientRows)
            entries.Add(new { resource = new { resourceType = "Patient", id = p.PatientId, name = p.FullName, gender = p.Gender } });
        foreach (var b in bookingRows)
            entries.Add(new { resource = new { resourceType = "Appointment", identifier = b.BookingNumber, status = b.Status, start = b.BookedAt } });

        var bundle = new { resourceType = "Bundle", type = "collection", total = entries.Count, entry = entries };
        var bundleJson = JsonSerializer.Serialize(bundle, JsonOptions);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bundleJson))).ToLowerInvariant();

        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE platform.data_export_requests
            SET status = 'ready', processing_completed_at = NOW(), file_checksum = @p1, file_size_bytes = @p2
            WHERE request_id = @p0
            """,
            new NpgsqlParameter("@p0", requestId),
            new NpgsqlParameter("@p1", checksum),
            new NpgsqlParameter("@p2", (long)Encoding.UTF8.GetByteCount(bundleJson)));

        return new DataExportResult(requestId, "fhir_r4_bundle", bundleJson, entries.Count, checksum);
    }

    private sealed record PatientRow(Guid PatientId, string? FullName, short? Age, string? Gender, string Lang);
    private sealed record BookingRow(string BookingNumber, string Status, DateTime BookedAt);
}

/// <summary>
/// DPDP §12 cryptographic erasure: destroys the subject's encryption key(s) via the KMS and records a
/// deletion certificate (keys destroyed + before/after hashes + signature). Ciphertext rows stay; they are
/// unrecoverable. Requires a deletion request row (FK).
/// </summary>
public sealed class CryptoErasureService(PlatformDbContext db, IKeyManagementService kms) : ICryptoErasureService
{
    public async Task<ErasureResult> EraseAsync(Guid deletionRequestId, string subjectPhone, Guid certifiedByUserId, CancellationToken ct)
    {
        // Identify the keys protecting this subject's data. For the local-dev provider we erase the
        // medical_history + aadhaar_partial data-class keys for the subject's tenants (the prerequisite for
        // the clinical PHI that 03b will encrypt). Find tenants the subject is linked to.
        var tenantIds = await db.Database.SqlQueryRaw<TenantRow>(
                """
                SELECT DISTINCT l.tenant_id AS "TenantId"
                FROM docslot.patient_tenant_links l JOIN docslot.patients p ON p.patient_id = l.patient_id
                WHERE p.phone_number = @p0
                """,
                new NpgsqlParameter("@p0", subjectPhone))
            .ToListAsync(ct);

        var preHash = await SubjectStateHashAsync(subjectPhone, ct);

        var destroyed = new List<Guid>();
        foreach (var t in tenantIds)
        foreach (var dataClass in new[] { "medical_history", "aadhaar_partial" })
        {
            var key = await kms.GetActiveKeyAsync(t.TenantId, dataClass, ct);
            await kms.DestroyKeyAsync(key.KeyId, certifiedByUserId, ct);
            destroyed.Add(key.KeyId);
        }

        var postHash = await SubjectStateHashAsync(subjectPhone, ct);

        var certificateId = Guid.CreateVersion7();
        var recordCounts = JsonSerializer.Serialize(new { tenants = tenantIds.Count, keys_destroyed = destroyed.Count });
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{certificateId}|{preHash}|{postHash}"))).ToLowerInvariant();

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform.deletion_certificates
                (certificate_id, deletion_request_id, subject_phone, deleted_record_counts, destroyed_key_ids,
                 destruction_method, pre_deletion_hash, post_deletion_hash, certified_by_user_id, certified_at, digital_signature)
            VALUES (@p0, @p1, @p2, CAST(@p3 AS jsonb), @p4, 'key_destruction', @p5, @p6, @p7, NOW(), @p8)
            """,
            new NpgsqlParameter("@p0", certificateId),
            new NpgsqlParameter("@p1", deletionRequestId),
            new NpgsqlParameter("@p2", subjectPhone),
            new NpgsqlParameter("@p3", recordCounts),
            new NpgsqlParameter("@p4", destroyed.ToArray()),
            new NpgsqlParameter("@p5", preHash),
            new NpgsqlParameter("@p6", postHash),
            new NpgsqlParameter("@p7", certifiedByUserId),
            new NpgsqlParameter("@p8", signature));

        return new ErasureResult(certificateId, destroyed, preHash, postHash);
    }

    private async Task<string> SubjectStateHashAsync(string subjectPhone, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<HashRow>(
                """
                SELECT COALESCE(string_agg(key_id::text || ':' || status, ',' ORDER BY key_id), '') AS "State"
                FROM platform.encryption_keys
                WHERE tenant_id IN (
                    SELECT l.tenant_id FROM docslot.patient_tenant_links l
                    JOIN docslot.patients p ON p.patient_id = l.patient_id WHERE p.phone_number = @p0)
                """,
                new NpgsqlParameter("@p0", subjectPhone))
            .ToListAsync(ct);
        var state = rows.FirstOrDefault()?.State ?? "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state))).ToLowerInvariant();
    }

    private sealed record TenantRow(Guid TenantId);
    private sealed record HashRow(string State);
}

/// <summary>DPDP §8(6) breach reporting into <c>platform.breach_log</c>.</summary>
public sealed class BreachReportingService(PlatformDbContext db) : IBreachReportingService
{
    public async Task<Guid> CreateAsync(string breachType, string severity, string description, Guid byUserId, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform.breach_log (breach_id, breach_type, severity, description, detected_at, created_at, created_by)
            VALUES (@p0, @p1, @p2, @p3, NOW(), NOW(), @p4)
            """,
            new NpgsqlParameter("@p0", id), new NpgsqlParameter("@p1", breachType),
            new NpgsqlParameter("@p2", severity), new NpgsqlParameter("@p3", description),
            new NpgsqlParameter("@p4", byUserId));
        return id;
    }

    public Task MarkReportedToDpbAsync(Guid breachId, Guid byUserId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE platform.breach_log SET reported_to_dpb_at = NOW() WHERE breach_id = @p0",
            new NpgsqlParameter("@p0", breachId));
}

/// <summary>DPDP §6 immutable consent event log into <c>platform.consent_event_log</c>.</summary>
public sealed class ConsentEventLogger(PlatformDbContext db) : IConsentEventLogger
{
    public Task RecordAsync(ConsentEvent e, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform.consent_event_log
                (event_id, patient_phone, tenant_id, event_type, consent_scope, legal_basis, channel, actor_user_id, ip_address, occurred_at)
            VALUES (gen_random_uuid(), @p0, @p1, @p2, CAST(@p3 AS jsonb), @p4, @p5, @p6, CAST(@p7 AS inet), NOW())
            """,
            new NpgsqlParameter("@p0", e.PatientPhone),
            new NpgsqlParameter("@p1", (object?)e.TenantId ?? DBNull.Value),
            new NpgsqlParameter("@p2", e.EventType),
            new NpgsqlParameter("@p3", e.ConsentScopeJson),
            new NpgsqlParameter("@p4", (object?)e.LegalBasis ?? DBNull.Value),
            new NpgsqlParameter("@p5", (object?)e.Channel ?? DBNull.Value),
            new NpgsqlParameter("@p6", (object?)e.ActorUserId ?? DBNull.Value),
            new NpgsqlParameter("@p7", (object?)e.IpAddress ?? DBNull.Value));
}

/// <summary>Audit-chain verification + anchoring (DPDP §8(7)).</summary>
public sealed class AuditChainService(PlatformDbContext db) : IAuditChainService
{
    public async Task<IReadOnlyList<AuditChainBreak>> VerifyAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<BreakRow>(
                """
                SELECT broken_at_sequence AS "Sequence", audit_id AS "AuditId",
                       expected_hash AS "ExpectedHash", actual_hash AS "ActualHash"
                FROM platform.verify_audit_chain()
                """)
            .ToListAsync(ct);
        return rows.Select(r => new AuditChainBreak(r.Sequence, r.AuditId, r.ExpectedHash, r.ActualHash)).ToList();
    }

    public async Task<AuditAnchorResult> AnchorAsync(string anchorType, string anchorReference, Guid byUserId, CancellationToken ct)
    {
        var heads = await db.Database.SqlQueryRaw<HeadRow>(
                "SELECT sequence_number AS \"Seq\", row_hash AS \"Hash\" FROM platform.audit_chain ORDER BY sequence_number DESC LIMIT 1")
            .ToListAsync(ct);
        var head = heads.FirstOrDefault() ?? new HeadRow(0, "0000000000000000000000000000000000000000000000000000000000000000");

        var anchorId = Guid.CreateVersion7();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform.audit_anchors (anchor_id, chain_head_sequence, chain_head_hash, anchored_at, anchor_type, anchor_reference, anchored_by_user_id)
            VALUES (@p0, @p1, @p2, NOW(), @p3, @p4, @p5)
            """,
            new NpgsqlParameter("@p0", anchorId), new NpgsqlParameter("@p1", head.Seq),
            new NpgsqlParameter("@p2", head.Hash), new NpgsqlParameter("@p3", anchorType),
            new NpgsqlParameter("@p4", anchorReference), new NpgsqlParameter("@p5", byUserId));

        return new AuditAnchorResult(anchorId, head.Seq, head.Hash);
    }

    private sealed record BreakRow(long Sequence, Guid AuditId, string ExpectedHash, string ActualHash);
    private sealed record HeadRow(long Seq, string Hash);
}

/// <summary>Break-glass emergency access (Layer 2) → purpose_of_use_log with is_break_glass + review_required.</summary>
public sealed class BreakGlassService(PlatformDbContext db) : IBreakGlassService
{
    public async Task<Guid> GrantAsync(Guid userId, Guid tenantId, string resourceType, Guid resourceId, string justification, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(justification))
            throw new ArgumentException("Break-glass requires a mandatory justification.", nameof(justification));

        var logId = Guid.CreateVersion7();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO platform.purpose_of_use_log
                (log_id, user_id, tenant_id, accessed_resource_type, accessed_resource_id, declared_purpose,
                 is_break_glass, break_glass_reason, accessed_at, review_required)
            VALUES (@p0, @p1, @p2, @p3, @p4, 'emergency', true, @p5, NOW(), true)
            """,
            new NpgsqlParameter("@p0", logId),
            new NpgsqlParameter("@p1", userId),
            new NpgsqlParameter("@p2", tenantId),
            new NpgsqlParameter("@p3", resourceType),
            new NpgsqlParameter("@p4", resourceId),
            new NpgsqlParameter("@p5", justification));
        return logId;
    }
}
