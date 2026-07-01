namespace mediq.SharedDataModel.Docslot.Admin;

// People — export + bulk import (issue #95, Phase D of epic #80). Staff PII (not PHI): a tenant's members,
// exported for offline editing / bulk re-import. The export is CSV-injection-safe (RFC-4180 quoting + leading
// =,+,-,@ neutralisation, mirroring the audit-log export). The import accepts a parsed JSON row list (the SPA
// parses the uploaded CSV client-side and posts JSON), provisions each row through the SAME escalation-safe
// single-user path, and returns a per-row result so one bad row never aborts the batch.

/// <summary>A ready-to-download CSV export of a tenant's members (no pagination; capped for safety).</summary>
public sealed record PeopleExportResult(string FileName, string Content);

/// <summary>One row of a bulk import. <c>RoleKey</c> is optional; when present, the role is conferred subject to
/// the R3 no-escalation guard (the actor may only confer roles they may confer singly).</summary>
public sealed record BulkImportUserRow(string Email, string FullName, string? RoleKey = null);

/// <summary>Bulk-import request body — a parsed row list. Capped at 500 rows (oversize is rejected 422).</summary>
public sealed record BulkImportUsersRequest(IReadOnlyList<BulkImportUserRow> Rows);

/// <summary>Per-row outcome. <c>Status</c> is one of created | linked | skipped | error. <c>Message</c> carries
/// the reason for a skip/error (e.g. the escalation-guard message for a non-conferrable role).</summary>
public sealed record BulkImportRowResult(int Row, string Email, string Status, string? Message);

/// <summary>Bulk-import summary: the per-row results plus rolled-up tallies for the import panel.</summary>
public sealed record BulkImportResult(
    int Total, int Created, int Linked, int Skipped, int Errored, IReadOnlyList<BulkImportRowResult> Rows);
