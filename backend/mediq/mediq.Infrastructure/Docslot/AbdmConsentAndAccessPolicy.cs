using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Docslot;

/// <summary>
/// ABDM consent gate over <c>docslot.abdm_consents</c>. A record read/push requires a 'granted',
/// unexpired consent for the patient in the requesting tenant.
/// </summary>
public sealed class AbdmConsentService(PlatformDbContext db) : IAbdmConsentService
{
    public async Task<bool> HasActiveConsentAsync(Guid patientId, Guid requestingTenantId, CancellationToken ct) =>
        await GetActiveConsentIdAsync(patientId, requestingTenantId, ct) is not null;

    public async Task<Guid?> GetActiveConsentIdAsync(Guid patientId, Guid requestingTenantId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<ConsentRow>(
                """
                SELECT consent_id AS "ConsentId"
                FROM docslot.abdm_consents
                WHERE patient_id = @p0 AND requesting_tenant_id = @p1
                  AND status = 'granted' AND expires_at > NOW() AND revoked_at IS NULL
                ORDER BY granted_at DESC NULLS LAST LIMIT 1
                """,
                new NpgsqlParameter("@p0", patientId), new NpgsqlParameter("@p1", requestingTenantId))
            .ToListAsync(ct);
        return rows.FirstOrDefault()?.ConsentId;
    }

    private sealed record ConsentRow(Guid ConsentId);
}

/// <summary>
/// Column/table access-policy awareness over <c>platform.access_policies</c>. For a (schema,table,column),
/// the caller must hold the required_permission of EVERY active policy gating it. Used as defense-in-depth
/// alongside RLS + purpose-of-use (e.g. a receptionist with patient.read but not medical history access).
/// </summary>
public sealed class AccessPolicyService(PlatformDbContext db) : IAccessPolicyService
{
    public async Task<bool> IsAllowedAsync(string schema, string table, string? column, IReadOnlySet<string> callerPermissions, CancellationToken ct)
    {
        // Policies that gate this table (column-specific OR table-wide where column_name IS NULL).
        var required = await db.Database.SqlQueryRaw<PermRow>(
                """
                SELECT DISTINCT required_permission AS "Perm"
                FROM platform.access_policies
                WHERE is_active = true AND schema_name = @p0 AND table_name = @p1
                  AND (column_name = @p2 OR column_name IS NULL)
                """,
                new NpgsqlParameter("@p0", schema), new NpgsqlParameter("@p1", table),
                new NpgsqlParameter("@p2", (object?)column ?? DBNull.Value))
            .ToListAsync(ct);

        // Allowed if there are no gating policies, or the caller holds every required permission.
        return required.All(r => callerPermissions.Contains(r.Perm));
    }

    private sealed record PermRow(string Perm);
}
