using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Npgsql;

namespace mediq.Infrastructure.Audit;

/// <summary>
/// Appends to the hash-chained <c>platform.audit_log</c>. The DB trigger <c>trg_audit_chain</c>
/// (database/05_security_hardening.sql) computes each row's chain hash from the previous row on INSERT,
/// so this writer must NOT compute hashes itself — it just inserts the business fields. NEVER deletes.
/// <para>
/// Writes on a DEDICATED connection (not the request's command transaction) so the audit record survives a
/// rollback of the surrounding business transaction — tamper-evidence must never depend on business success.
/// </para>
/// </summary>
public sealed class AuditTrailWriter(IDedicatedConnectionFactory connections) : IAuditTrailWriter
{
    public async Task RecordAsync(AuditEntry e, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO platform.audit_log
                (audit_id, occurred_at, user_id, ip_address, user_agent, correlation_id, tenant_id,
                 action, resource_type, resource_id, resource_label, change_summary, purpose, legal_basis, success)
            VALUES
                (gen_random_uuid(), NOW(), @p0, CAST(@p1 AS inet), @p2, @p3, @p4,
                 @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12)
            """, conn);
        cmd.Parameters.AddWithValue("@p0", (object?)e.UserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p1", (object?)e.IpAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p2", (object?)e.UserAgent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p3", (object?)e.CorrelationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p4", (object?)e.TenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p5", e.Action);
        cmd.Parameters.AddWithValue("@p6", e.ResourceType);
        cmd.Parameters.AddWithValue("@p7", (object?)e.ResourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p8", (object?)e.ResourceLabel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p9", (object?)e.ChangeSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p10", (object?)e.Purpose ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p11", (object?)e.LegalBasis ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p12", e.Success);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
