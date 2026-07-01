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
public sealed class SecurityReadService(PlatformDbContext db, IGeoIpResolver geo) : ISecurityReadService
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

    // ---- Audit tab: READ side of the WRITE-only platform.audit_log (issue #86) --------------------
    // SECURITY NOTE (audit sign-off): platform.audit_log has NO RLS policy — only a tenant_id column +
    // idx_audit_tenant. The explicit `al.tenant_id = @tenant` predicate below (bound from the server-signed
    // ICurrentUserContext.TenantId, never a query param) is therefore the SOLE tenant-isolation guard, so ANY
    // future audit read/aggregation MUST carry it. The projection deliberately stops at resource_label — that
    // is the accepted PHI boundary for this surface; before_data/after_data/change_summary/error_message and
    // the hash-chain columns are never selected.

    public async Task<AuditLogPageDto> ReadAuditLogAsync(Guid? tenantId, AuditLogFilter filter, CancellationToken ct)
    {
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, 200);

        // A null tenant can never match a row (audit_log.tenant_id is never NULL for tenant activity) → empty.
        if (tenantId is null)
            return new AuditLogPageDto(page, size, 0, [], [], [], filter.From, filter.To);

        // The base predicate (tenant + window + free-text) is shared by the page, the total, and the facets.
        // The category/severity SELECTION is layered on ONLY for the page + total, so the facet rails stay
        // independent of what's currently selected.
        var baseSpecs = new List<(string Name, object Value)>
        {
            ("@tenant", tenantId.Value),
            ("@from", filter.From),
            ("@to", filter.To),
            ("@dangerous", AuditTaxonomy.DangerousActions.ToArray()),
        };
        var baseWhere = "al.tenant_id = @tenant AND al.occurred_at >= @from AND al.occurred_at < @to";

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            baseSpecs.Add(("@search", "%" + filter.Search.Trim() + "%"));
            baseWhere += " AND (al.action ILIKE @search OR al.resource_type ILIKE @search "
                       + "OR al.resource_label ILIKE @search OR u.full_name ILIKE @search OR u.email ILIKE @search)";
        }

        // The selection layer (category + severity) — appended for the page/total only.
        var selSpecs = new List<(string Name, object Value)>(baseSpecs);
        var fullWhere = baseWhere;
        AppendCategoryFilter(filter.Category, ref fullWhere, selSpecs);
        AppendSeverityFilter(filter.Severity, ref fullWhere, selSpecs);

        // Total (full selection).
        var totalRows = await db.Database.SqlQueryRaw<CountRow>(
            $"SELECT count(*)::int AS \"Value\" FROM platform.audit_log al "
            + "LEFT JOIN platform.users u ON u.user_id = al.user_id "
            + $"WHERE {fullWhere}", Fresh(selSpecs)).ToListAsync(ct);
        var total = totalRows.FirstOrDefault()?.Value ?? 0;

        // The page itself (full selection), newest first.
        var pageSpecs = new List<(string Name, object Value)>(selSpecs)
        {
            ("@limit", size), ("@offset", (page - 1) * size),
        };
        var rows = await db.Database.SqlQueryRaw<AuditPageRow>(
            $"""
             SELECT al.audit_id AS "AuditId", al.occurred_at AS "OccurredAt", al.user_id AS "ActorUserId",
                    u.full_name AS "ActorName", u.email AS "ActorEmail",
                    al.impersonator_user_id AS "ImpersonatorUserId", imp.full_name AS "ImpersonatorName",
                    al.action AS "RawAction", al.resource_type AS "ResourceType", al.resource_label AS "ResourceLabel",
                    al.resource_id AS "ResourceId", al.ip_address::text AS "IpAddress",
                    al.success AS "Success", al.error_code AS "ErrorCode"
             FROM platform.audit_log al
             LEFT JOIN platform.users u ON u.user_id = al.user_id
             LEFT JOIN platform.users imp ON imp.user_id = al.impersonator_user_id
             WHERE {fullWhere}
             ORDER BY al.occurred_at DESC
             LIMIT @limit OFFSET @offset
             """, Fresh(pageSpecs)).ToListAsync(ct);

        // Facets over the BASE set (no category/severity selection).
        var catFacetRows = await db.Database.SqlQueryRaw<CategoryFacetRow>(
            $"SELECT al.resource_type AS \"ResourceType\", count(*)::int AS \"Count\" "
            + "FROM platform.audit_log al LEFT JOIN platform.users u ON u.user_id = al.user_id "
            + $"WHERE {baseWhere} GROUP BY al.resource_type", Fresh(baseSpecs)).ToListAsync(ct);

        var sevFacetRows = await db.Database.SqlQueryRaw<SeverityFacetRow>(
            $"SELECT al.success AS \"Success\", (al.action = ANY(@dangerous)) AS \"Dangerous\", count(*)::int AS \"Count\" "
            + "FROM platform.audit_log al LEFT JOIN platform.users u ON u.user_id = al.user_id "
            + $"WHERE {baseWhere} GROUP BY al.success, (al.action = ANY(@dangerous))", Fresh(baseSpecs)).ToListAsync(ct);

        var categoryFacets = catFacetRows
            .GroupBy(r => AuditTaxonomy.MapCategory(r.ResourceType))
            .Select(g => new AuditFacetCount(g.Key, g.Sum(x => x.Count)))
            .OrderBy(f => AuditTaxonomy.Categories.ToList().IndexOf(f.Key))
            .ToList();

        var severityFacets = sevFacetRows
            .GroupBy(r => AuditTaxonomy.ClassifyByFlags(r.Success, r.Dangerous))
            .Select(g => new AuditFacetCount(g.Key, g.Sum(x => x.Count)))
            .OrderBy(f => AuditTaxonomy.Severities.ToList().IndexOf(f.Key))
            .ToList();

        // #94: enrich each page row with a geo-IP city via the seam (one lookup per distinct IP; null offline,
        // so the UI shows just the IP). The export path deliberately stays city-less (unchanged CSV columns).
        var cities = await ResolveCitiesAsync(rows.Select(r => r.IpAddress), ct);
        var items = rows.Select(r => MapRow(r) with { City = CityFor(cities, r.IpAddress) }).ToList();

        return new AuditLogPageDto(page, size, total, items,
            categoryFacets, severityFacets, filter.From, filter.To);
    }

    /// <summary>Resolve the distinct non-empty IPs to a city map (one lookup per unique IP; null offline).</summary>
    private async Task<Dictionary<string, string?>> ResolveCitiesAsync(IEnumerable<string?> ips, CancellationToken ct)
    {
        var map = new Dictionary<string, string?>();
        foreach (var ip in ips.Where(ip => !string.IsNullOrEmpty(ip)).Distinct())
            map[ip!] = await geo.ResolveCityAsync(ip, ct);
        return map;
    }

    private static string? CityFor(IReadOnlyDictionary<string, string?> map, string? ip) =>
        ip is not null && map.TryGetValue(ip, out var c) ? c : null;

    public async Task<IReadOnlyList<AuditLogRowDto>> ReadAuditLogRowsForExportAsync(
        Guid? tenantId, AuditLogFilter filter, int cap, CancellationToken ct)
    {
        if (tenantId is null) return [];

        var specs = new List<(string Name, object Value)>
        {
            ("@tenant", tenantId.Value),
            ("@from", filter.From),
            ("@to", filter.To),
            ("@dangerous", AuditTaxonomy.DangerousActions.ToArray()),
        };
        var where = "al.tenant_id = @tenant AND al.occurred_at >= @from AND al.occurred_at < @to";
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            specs.Add(("@search", "%" + filter.Search.Trim() + "%"));
            where += " AND (al.action ILIKE @search OR al.resource_type ILIKE @search "
                   + "OR al.resource_label ILIKE @search OR u.full_name ILIKE @search OR u.email ILIKE @search)";
        }
        AppendCategoryFilter(filter.Category, ref where, specs);
        AppendSeverityFilter(filter.Severity, ref where, specs);
        specs.Add(("@cap", Math.Clamp(cap, 1, 50_000)));

        var rows = await db.Database.SqlQueryRaw<AuditPageRow>(
            $"""
             SELECT al.audit_id AS "AuditId", al.occurred_at AS "OccurredAt", al.user_id AS "ActorUserId",
                    u.full_name AS "ActorName", u.email AS "ActorEmail",
                    al.impersonator_user_id AS "ImpersonatorUserId", imp.full_name AS "ImpersonatorName",
                    al.action AS "RawAction", al.resource_type AS "ResourceType", al.resource_label AS "ResourceLabel",
                    al.resource_id AS "ResourceId", al.ip_address::text AS "IpAddress",
                    al.success AS "Success", al.error_code AS "ErrorCode"
             FROM platform.audit_log al
             LEFT JOIN platform.users u ON u.user_id = al.user_id
             LEFT JOIN platform.users imp ON imp.user_id = al.impersonator_user_id
             WHERE {where}
             ORDER BY al.occurred_at DESC
             LIMIT @cap
             """, Fresh(specs)).ToListAsync(ct);

        return rows.Select(MapRow).ToList();
    }

    private static AuditLogRowDto MapRow(AuditPageRow r) => new(
        r.AuditId, Utc(r.OccurredAt), r.ActorUserId, r.ActorName, r.ActorEmail,
        r.ImpersonatorUserId, r.ImpersonatorName,
        AuditTaxonomy.Humanize(r.RawAction), r.RawAction, r.ResourceType, r.ResourceLabel, r.ResourceId,
        AuditTaxonomy.MapCategory(r.ResourceType), AuditTaxonomy.Classify(r.Success, r.RawAction),
        r.IpAddress, r.Success, r.ErrorCode);

    /// <summary>Push a category selection down to SQL as a resource_type membership predicate (or its inverse for Other).</summary>
    private static void AppendCategoryFilter(string? category, ref string where, List<(string Name, object Value)> specs)
    {
        if (string.IsNullOrWhiteSpace(category) || !AuditTaxonomy.IsKnownCategory(category)) return;
        if (category.Equals(AuditTaxonomy.Other, StringComparison.OrdinalIgnoreCase))
        {
            specs.Add(("@mappedTypes", AuditTaxonomy.MappedResourceTypes.ToArray()));
            where += " AND NOT (al.resource_type = ANY(@mappedTypes))";
        }
        else
        {
            specs.Add(("@catTypes", AuditTaxonomy.ResourceTypesForCategory(category)));
            where += " AND al.resource_type = ANY(@catTypes)";
        }
    }

    /// <summary>Push a severity selection down to SQL using the same success + dangerous-action heuristic as the row DTO.</summary>
    private static void AppendSeverityFilter(string? severity, ref string where, List<(string Name, object Value)> specs)
    {
        if (string.IsNullOrWhiteSpace(severity)) return;
        // @dangerous is always present in specs (added by the caller).
        if (severity.Equals(AuditTaxonomy.Critical, StringComparison.OrdinalIgnoreCase))
            where += " AND al.success = false AND (al.action = ANY(@dangerous))";
        else if (severity.Equals(AuditTaxonomy.Warning, StringComparison.OrdinalIgnoreCase))
            where += " AND ((al.success = false AND NOT (al.action = ANY(@dangerous))) "
                   + "OR (al.success = true AND (al.action = ANY(@dangerous))))";
        else if (severity.Equals(AuditTaxonomy.Informational, StringComparison.OrdinalIgnoreCase))
            where += " AND al.success = true AND NOT (al.action = ANY(@dangerous))";
    }

    /// <summary>Materialize a fresh NpgsqlParameter[] per command (parameter instances can't be shared across commands).</summary>
    private static object[] Fresh(IEnumerable<(string Name, object Value)> specs) =>
        specs.Select(s => (object)new NpgsqlParameter(s.Name, s.Value)).ToArray();

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

    private sealed record CountRow(int Value);
    private sealed record CategoryFacetRow(string ResourceType, int Count);
    private sealed record SeverityFacetRow(bool Success, bool Dangerous, int Count);
    private sealed record AuditPageRow(
        Guid AuditId, DateTime OccurredAt, Guid? ActorUserId, string? ActorName, string? ActorEmail,
        Guid? ImpersonatorUserId, string? ImpersonatorName, string RawAction, string ResourceType,
        string? ResourceLabel, Guid? ResourceId, string? IpAddress, bool Success, string? ErrorCode);
}
