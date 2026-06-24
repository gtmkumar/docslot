using System.Text.Json;
using mediq.Application.Abstractions;
using mediq.Infrastructure.Docslot;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Read side of the Security &amp; Compliance console (slice 05). Projects the anchor history, DPDP rights
/// requests, breach register, review queue, key-rotation status, and deletion certificates. SENSITIVE:
/// the subject phone is MASKED here (<see cref="PhoneMasker"/>) — raw subject_phone never leaves this seam;
/// key rows carry NO key material (only metadata). Reads use a direct reader for array/jsonb columns.
/// </summary>
public sealed class SecurityReadService(PlatformDbContext db) : ISecurityReadService
{
    public async Task<IReadOnlyList<AuditAnchorDto>> ListAnchorsAsync(int take, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<AnchorRow>(
                """
                SELECT anchor_id AS "AnchorId", chain_head_sequence AS "ChainHeadSequence",
                       chain_head_hash AS "ChainHeadHash", anchor_type AS "AnchorType",
                       anchor_reference AS "AnchorReference", anchored_at AS "AnchoredAt"
                FROM platform.audit_anchors ORDER BY anchored_at DESC LIMIT @p0
                """, P(("@p0", take)))
            .ToListAsync(ct);
        return rows.Select(r => new AuditAnchorDto(
            r.AnchorId, r.ChainHeadSequence, r.ChainHeadHash, r.AnchorType, r.AnchorReference, Utc(r.AnchoredAt))).ToList();
    }

    public async Task<DateTimeOffset?> GetLastAnchorAtAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<TimeRow>(
                "SELECT MAX(anchored_at) AS \"At\" FROM platform.audit_anchors", Array.Empty<object>())
            .ToListAsync(ct);
        var at = rows.FirstOrDefault()?.At;
        return at is null ? null : Utc(at.Value);
    }

    public async Task<IReadOnlyList<DpdpRequestDto>> ListDpdpRequestsAsync(int take, CancellationToken ct)
    {
        // Unify export + deletion requests into one rights-request feed (most recent first).
        var rows = await db.Database.SqlQueryRaw<DpdpRow>(
                """
                SELECT request_id AS "RequestId", 'export' AS "Kind", subject_phone AS "SubjectPhone",
                       status AS "Status", COALESCE(array_to_string(scope_data_classes, ','), 'all') AS "Scope",
                       rejection_reason AS "Reason", NULL::timestamptz AS "GracePeriodEndsAt", created_at AS "CreatedAt"
                FROM platform.data_export_requests
                UNION ALL
                SELECT request_id, 'erasure', subject_phone, status, scope,
                       reason, grace_period_ends_at, created_at
                FROM platform.data_deletion_requests
                ORDER BY "CreatedAt" DESC LIMIT @p0
                """, P(("@p0", take)))
            .ToListAsync(ct);
        return rows.Select(r => new DpdpRequestDto(
            r.RequestId, r.Kind, PhoneMasker.Mask(r.SubjectPhone), r.Status, r.Scope, r.Reason,
            r.GracePeriodEndsAt is null ? null : Utc(r.GracePeriodEndsAt.Value), Utc(r.CreatedAt))).ToList();
    }

    public async Task<IReadOnlyList<BreachDto>> ListBreachesAsync(int take, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<BreachRow>(
                """
                SELECT breach_id AS "BreachId", breach_type AS "BreachType", severity AS "Severity",
                       description AS "Description", affected_record_count AS "AffectedRecordCount",
                       detected_at AS "DetectedAt", reported_to_dpb_at AS "ReportedToDpbAt", resolved_at AS "ResolvedAt"
                FROM platform.breach_log ORDER BY detected_at DESC LIMIT @p0
                """, P(("@p0", take)))
            .ToListAsync(ct);
        return rows.Select(r => new BreachDto(
            r.BreachId, r.BreachType, r.Severity, r.Description, r.AffectedRecordCount,
            Utc(r.DetectedAt), r.ReportedToDpbAt is null ? null : Utc(r.ReportedToDpbAt.Value),
            r.ResolvedAt is null ? null : Utc(r.ResolvedAt.Value))).ToList();
    }

    public async Task<IReadOnlyList<ReviewQueueItemDto>> ListReviewQueueAsync(int take, CancellationToken ct)
    {
        // The view exposes source/item/severity/occurred_at/description + the acting user_id (no subject phone).
        // We surface a masked actor label (no email/PHI); subject phone is not in the view → null.
        var rows = await db.Database.SqlQueryRaw<ReviewRow>(
                """
                SELECT q.source AS "Source", q.item_id AS "ItemId", q.severity AS "Severity",
                       q.occurred_at AS "OccurredAt", q.description AS "Description",
                       u.full_name AS "ActorName"
                FROM platform.v_security_review_queue q
                LEFT JOIN platform.users u ON u.user_id = q.user_id
                ORDER BY q.occurred_at DESC LIMIT @p0
                """, P(("@p0", take)))
            .ToListAsync(ct);
        return rows.Select(r => new ReviewQueueItemDto(
            r.Source, r.ItemId, r.Severity, Utc(r.OccurredAt), r.Description, ActorInitials(r.ActorName), null)).ToList();
    }

    public async Task<IReadOnlyList<KeyStatusDto>> ListKeyStatusAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<KeyRow>(
                """
                SELECT key_id AS "KeyId", tenant_name AS "TenantName", data_class AS "DataClass",
                       activated_at AS "ActivatedAt", next_rotation_due_at AS "NextRotationDueAt",
                       rotation_status AS "RotationStatus", days_until_rotation AS "DaysUntilRotation",
                       usage_count AS "UsageCount"
                FROM platform.v_key_rotation_status ORDER BY next_rotation_due_at NULLS LAST
                """, Array.Empty<object>())
            .ToListAsync(ct);
        return rows.Select(r => new KeyStatusDto(
            r.KeyId, r.TenantName, r.DataClass, Utc(r.ActivatedAt),
            r.NextRotationDueAt is null ? null : Utc(r.NextRotationDueAt.Value),
            r.RotationStatus, r.DaysUntilRotation, r.UsageCount)).ToList();
    }

    public async Task<IReadOnlyList<DeletionCertificateDto>> ListDeletionCertificatesAsync(int take, CancellationToken ct)
    {
        // Array (uuid[]) + jsonb columns → read via a direct NpgsqlDataReader (avoids EF mapping pitfalls).
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT certificate_id, deletion_request_id, subject_phone, destroyed_key_ids,
                   pre_deletion_hash, post_deletion_hash, signature_algorithm, digital_signature,
                   certified_at, deleted_record_counts::text
            FROM platform.deletion_certificates ORDER BY certified_at DESC LIMIT @p0
            """, conn);
        cmd.Parameters.AddWithValue("@p0", take);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var result = new List<DeletionCertificateDto>();
        while (await rd.ReadAsync(ct))
        {
            var keyIds = rd.IsDBNull(3) ? Array.Empty<Guid>() : (Guid[])rd.GetValue(3);
            var countsJson = rd.IsDBNull(9) ? "{}" : rd.GetString(9);
            var counts = JsonSerializer.Deserialize<Dictionary<string, int>>(countsJson) ?? new();
            result.Add(new DeletionCertificateDto(
                rd.GetGuid(0), rd.GetGuid(1), PhoneMasker.Mask(rd.IsDBNull(2) ? null : rd.GetString(2)),
                keyIds, rd.IsDBNull(4) ? "" : rd.GetString(4), rd.IsDBNull(5) ? "" : rd.GetString(5),
                rd.IsDBNull(6) ? "" : rd.GetString(6), rd.IsDBNull(7) ? "" : rd.GetString(7),
                Utc(rd.GetDateTime(8)), counts));
        }
        return result;
    }

    public async Task<IReadOnlyList<ImpersonationSessionDto>> ListImpersonationSessionsAsync(int take, CancellationToken ct)
    {
        // platform.list_impersonation_sessions (SECURITY DEFINER) reads past the super-only RLS and derives
        // the status; metadata only (no PHI). We mask the actor to initials here, mirroring the review queue.
        var rows = await db.Database.SqlQueryRaw<ImpersonationRow>(
                """
                SELECT impersonation_id AS "ImpersonationId", actor_name AS "ActorName",
                       target_tenant_id AS "TargetTenantId", target_tenant_name AS "TargetTenantName",
                       target_user_id AS "TargetUserId", reason AS "Reason", is_break_glass AS "IsBreakGlass",
                       started_at AS "StartedAt", expires_at AS "ExpiresAt", ended_at AS "EndedAt", status AS "Status"
                FROM platform.list_impersonation_sessions(@p0)
                """, P(("@p0", take)))
            .ToListAsync(ct);
        return rows.Select(r => new ImpersonationSessionDto(
            r.ImpersonationId, ActorInitials(r.ActorName), r.TargetTenantId, r.TargetTenantName, r.TargetUserId,
            r.Reason, r.IsBreakGlass, Utc(r.StartedAt), Utc(r.ExpiresAt),
            r.EndedAt is null ? null : Utc(r.EndedAt.Value), r.Status)).ToList();
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static DateTimeOffset Utc(DateTime dt) => new(DateTime.SpecifyKind(dt, DateTimeKind.Utc));

    /// <summary>Display-safe actor label — initials only, never the full name/email (PHI minimisation).</summary>
    private static string? ActorInitials(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return null;
        var parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(".", parts.Select(p => char.ToUpperInvariant(p[0]))) + ".";
    }

    private static object[] P(params (string Name, object Value)[] ps) =>
        ps.Select(p => (object)new NpgsqlParameter(p.Name, p.Value)).ToArray();

    private sealed record AnchorRow(Guid AnchorId, long ChainHeadSequence, string ChainHeadHash, string AnchorType, string AnchorReference, DateTime AnchoredAt);
    private sealed record TimeRow(DateTime? At);
    private sealed record DpdpRow(Guid RequestId, string Kind, string? SubjectPhone, string Status, string Scope, string? Reason, DateTime? GracePeriodEndsAt, DateTime CreatedAt);
    private sealed record BreachRow(Guid BreachId, string BreachType, string Severity, string Description, int? AffectedRecordCount, DateTime DetectedAt, DateTime? ReportedToDpbAt, DateTime? ResolvedAt);
    private sealed record ReviewRow(string Source, Guid ItemId, string Severity, DateTime OccurredAt, string Description, string? ActorName);
    private sealed record KeyRow(Guid KeyId, string? TenantName, string DataClass, DateTime ActivatedAt, DateTime? NextRotationDueAt, string RotationStatus, int? DaysUntilRotation, long UsageCount);
    private sealed record ImpersonationRow(Guid ImpersonationId, string? ActorName, Guid TargetTenantId, string? TargetTenantName, Guid? TargetUserId, string Reason, bool IsBreakGlass, DateTime StartedAt, DateTime ExpiresAt, DateTime? EndedAt, string Status);
}
