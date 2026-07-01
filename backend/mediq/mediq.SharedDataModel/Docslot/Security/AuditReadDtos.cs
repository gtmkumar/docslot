namespace mediq.SharedDataModel.Docslot.Security;

// Read DTOs for the Audit tab (issue #86 — the read side of the WRITE-only platform.audit_log).
// SENSITIVE SURFACE:
//  - The actor identity (name + email) IS included as distinct fields — the FE/auditor decides masking.
//  - No hash-chain internals (prev/curr hash, sequence) and no PHI beyond resource_label are ever serialised
//    (before_data / after_data / change_summary are deliberately NOT projected).

/// <summary>
/// One audit-log row, projected for the Audit tab. Category + Severity are DERIVED
/// (see <see cref="AuditTaxonomy"/>); <see cref="Action"/> is the humanized label and <see cref="RawAction"/>
/// the original verb. The actor identity is included as distinct fields so the client can choose to mask it.
/// </summary>
public sealed record AuditLogRowDto(
    Guid AuditId,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string? ActorName,
    string? ActorEmail,
    Guid? ImpersonatorUserId,
    string? ImpersonatorName,
    string Action,                 // humanized, e.g. "Break Glass"
    string RawAction,              // original verb, e.g. "break_glass"
    string ResourceType,
    string? ResourceLabel,
    Guid? ResourceId,
    string Category,               // Bookings | Patients | Payments | Team | Settings | Security | Analytics | Other
    string Severity,               // Informational | Warning | Critical
    string? IpAddress,             // raw IP
    bool Success,
    string? ErrorCode,
    string? City = null);          // geo-IP city (issue #94) — null offline (NullGeoIpResolver); UI then shows just the IP

/// <summary>A single facet bucket (category or severity) with its count over the filtered set.</summary>
public sealed record AuditFacetCount(string Key, int Count);

/// <summary>
/// A page of audit rows plus the faceted counts for the filter rails. The facets are computed over the
/// date-range + free-text-filtered set and are INDEPENDENT of the selected category/severity, so the rails
/// always show what you could switch to. <see cref="From"/>/<see cref="To"/> echo the resolved window.
/// </summary>
public sealed record AuditLogPageDto(
    int Page,
    int PageSize,
    int Total,
    IReadOnlyList<AuditLogRowDto> Items,
    IReadOnlyList<AuditFacetCount> CategoryFacets,
    IReadOnlyList<AuditFacetCount> SeverityFacets,
    DateTimeOffset From,
    DateTimeOffset To);

/// <summary>Normalised, defaulted filter for an audit read (window defaults to the last 30 days).</summary>
public sealed record AuditLogFilter(
    int Page,
    int PageSize,
    DateTimeOffset From,
    DateTimeOffset To,
    string? Category,
    string? Severity,
    string? Search);

/// <summary>A ready-to-download CSV export of the filtered audit rows (no pagination; capped for safety).</summary>
public sealed record AuditCsvResult(string FileName, string Content);
