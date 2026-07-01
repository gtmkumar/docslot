using System.Text.Json;
using System.Text.Json.Nodes;
using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Security;

/// <summary>
/// Stores the tenant security policy (issue #91) in <c>platform.tenants.settings-&gt;'security'</c> — NO new
/// table. Reads merge stored keys over <see cref="SecurityPolicy.Default"/> so partial/absent config still
/// yields a complete policy; the write uses <c>jsonb_set</c> so the rest of the settings blob is untouched.
/// platform.tenants has no RLS, so every statement is explicitly scoped by <c>tenant_id</c>.
/// </summary>
public sealed class TenantSecurityPolicyService(PlatformDbContext db) : ITenantSecurityPolicyService
{
    public async Task<SecurityPolicy> GetAsync(Guid tenantId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<PolicyJsonRow>(
                """SELECT (settings->'security')::text AS "Json" FROM platform.tenants WHERE tenant_id = @p0""",
                new NpgsqlParameter("@p0", tenantId))
            .ToListAsync(ct);

        var json = rows.FirstOrDefault()?.Json;
        return Parse(json);
    }

    public async Task SaveAsync(Guid tenantId, SecurityPolicy policy, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            mfaPolicy = policy.MfaPolicy,
            minPasswordLength = policy.MinPasswordLength,
            idleTimeoutMinutes = policy.IdleTimeoutMinutes,
            requireNewDeviceVerification = policy.RequireNewDeviceVerification,
            restrictLoginHours = policy.RestrictLoginHours,
            loginHoursStart = policy.LoginHoursStart,
            loginHoursEnd = policy.LoginHoursEnd,
            doctorsExemptFromHours = policy.DoctorsExemptFromHours,
            ipAllowlistEnabled = policy.IpAllowlistEnabled,
            maskSensitiveForReceptionist = policy.MaskSensitiveForReceptionist,
        });

        // jsonb_set with create_missing=true replaces ONLY the 'security' key; COALESCE guards a NULL settings
        // blob. NOTE: EF's ExecuteSqlRaw treats '{...}' as a positional placeholder, so brace literals are
        // avoided here — jsonb_build_object() yields an empty '{}' and ARRAY['security'] is the text[] path.
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE platform.tenants
            SET settings = jsonb_set(COALESCE(settings, jsonb_build_object()), ARRAY['security'], CAST(@p1 AS jsonb), true),
                updated_at = NOW()
            WHERE tenant_id = @p0
            """,
            [new NpgsqlParameter("@p0", tenantId), new NpgsqlParameter("@p1", json)], ct);
    }

    public async Task<int> CountStaffPendingMfaEnrolmentAsync(Guid tenantId, SecurityPolicy policy, CancellationToken ct)
    {
        if (string.Equals(policy.MfaPolicy, MfaPolicyTiers.Optional, StringComparison.Ordinal))
            return 0;

        // 'all' → every active member without mfa; 'owners_admins' → only members who hold an owner/admin key.
        string sql;
        if (string.Equals(policy.MfaPolicy, MfaPolicyTiers.All, StringComparison.Ordinal))
            sql =
                """
                SELECT COUNT(DISTINCT u.user_id)::int AS "Value"
                FROM platform.users u
                JOIN platform.user_tenant_roles utr
                  ON utr.user_id = u.user_id AND utr.tenant_id = @p0 AND utr.revoked_at IS NULL
                WHERE u.is_active AND u.deleted_at IS NULL AND u.mfa_enabled = false
                """;
        else
            sql =
                """
                SELECT COUNT(DISTINCT u.user_id)::int AS "Value"
                FROM platform.users u
                JOIN platform.user_tenant_roles utr
                  ON utr.user_id = u.user_id AND utr.tenant_id = @p0 AND utr.revoked_at IS NULL
                JOIN platform.role_permissions rp ON rp.role_id = utr.role_id
                JOIN platform.permissions p ON p.permission_id = rp.permission_id
                WHERE u.is_active AND u.deleted_at IS NULL AND u.mfa_enabled = false
                  AND p.permission_key = ANY(@p1)
                """;

        var ps = string.Equals(policy.MfaPolicy, MfaPolicyTiers.All, StringComparison.Ordinal)
            ? new[] { new NpgsqlParameter("@p0", tenantId) }
            : [new NpgsqlParameter("@p0", tenantId), new NpgsqlParameter("@p1", SecurityPolicyPermissions.OwnerAdminKeys)];

        var rows = await db.Database.SqlQueryRaw<IntRow>(sql, ps).ToListAsync(ct);
        return rows.FirstOrDefault()?.Value ?? 0;
    }

    /// <summary>Merges stored JSON over the defaults, tolerating absent keys / a NULL blob.</summary>
    internal static SecurityPolicy Parse(string? json)
    {
        var d = SecurityPolicy.Default;
        if (string.IsNullOrWhiteSpace(json)) return d;

        JsonObject? o;
        try { o = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return d; }
        if (o is null) return d;

        return new SecurityPolicy(
            MfaPolicy: Str(o, "mfaPolicy", d.MfaPolicy),
            MinPasswordLength: Int(o, "minPasswordLength", d.MinPasswordLength),
            IdleTimeoutMinutes: Int(o, "idleTimeoutMinutes", d.IdleTimeoutMinutes),
            RequireNewDeviceVerification: Bool(o, "requireNewDeviceVerification", d.RequireNewDeviceVerification),
            RestrictLoginHours: Bool(o, "restrictLoginHours", d.RestrictLoginHours),
            LoginHoursStart: Str(o, "loginHoursStart", d.LoginHoursStart),
            LoginHoursEnd: Str(o, "loginHoursEnd", d.LoginHoursEnd),
            DoctorsExemptFromHours: Bool(o, "doctorsExemptFromHours", d.DoctorsExemptFromHours),
            IpAllowlistEnabled: Bool(o, "ipAllowlistEnabled", d.IpAllowlistEnabled),
            MaskSensitiveForReceptionist: Bool(o, "maskSensitiveForReceptionist", d.MaskSensitiveForReceptionist));
    }

    private static string Str(JsonObject o, string k, string fallback) =>
        o.TryGetPropertyValue(k, out var n) && n is not null ? n.GetValue<string>() : fallback;

    private static int Int(JsonObject o, string k, int fallback)
    {
        if (o.TryGetPropertyValue(k, out var n) && n is JsonValue v && v.TryGetValue<int>(out var i)) return i;
        return fallback;
    }

    private static bool Bool(JsonObject o, string k, bool fallback)
    {
        if (o.TryGetPropertyValue(k, out var n) && n is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return fallback;
    }

    private sealed record PolicyJsonRow(string? Json);
    private sealed record IntRow(int Value);
}
