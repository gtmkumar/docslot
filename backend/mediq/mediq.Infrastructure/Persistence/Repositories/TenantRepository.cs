using mediq.Application.Abstractions;
using mediq.Domain.Platform;
using mediq.Utilities.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Persistence.Repositories;

public sealed class TenantRepository(PlatformDbContext db) : ITenantRepository
{
    public Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken ct) =>
        db.Tenants.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.DeletedAt == null, ct);

    public async Task<IReadOnlyList<Tenant>> ListAsync(int skip, int take, CancellationToken ct) =>
        await db.Tenants.AsNoTracking()
            .Where(t => t.DeletedAt == null)
            .OrderBy(t => t.DisplayName)
            .Skip(skip).Take(take)
            .ToListAsync(ct);

    /// <summary>
    /// The tenants a user can switch into. This is a LOGIN-TIME, CROSS-TENANT self-read: it runs before any
    /// tenant context (and thus <c>app.tenant_id</c>) exists, so a direct EF read of <c>user_tenant_roles</c>
    /// is now blocked by the Row-Level Security enabled in database/11_rbac_hardening.sql (the <c>utr_read</c>
    /// policy requires the row's tenant to equal <c>current_tenant_id()</c>). The hardening file's design note
    /// mandates that login-time cross-tenant lookups go through a SECURITY DEFINER / owner-rights path.
    /// <para>
    /// We therefore source the switch-list from the purpose-built <c>platform.user_memberships(uuid)</c>
    /// SECURITY DEFINER function (owner-rights, so it bypasses R1 RLS; filtered to the caller's own
    /// <c>user_id</c>, so there is no cross-tenant leak). It preserves the original semantics — every active,
    /// non-deleted membership, including <c>is_primary</c> — rather than only tenants where the user has
    /// resolved permissions. The query runs on the DbContext connection (house pattern:
    /// <see cref="RelationalQueryableExtensions.FromSqlRaw"/>).
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<UserTenantMembership>> GetMembershipsAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.UserMembershipRows
            .FromSqlRaw(
                """
                SELECT
                    tenant_id    AS "TenantId",
                    tenant_code  AS "TenantCode",
                    display_name AS "DisplayName",
                    tenant_type  AS "TenantType",
                    is_primary   AS "IsPrimary"
                FROM platform.user_memberships(@p0)
                """,
                new NpgsqlParameter("@p0", userId))
            .AsNoTracking()
            .ToListAsync(ct);

        return rows
            .Select(r => new UserTenantMembership(r.TenantId, r.TenantCode, r.DisplayName, r.TenantType, r.IsPrimary))
            .ToList();
    }

    /// <summary>
    /// Display-only role labels for GET /me. Runs in REQUEST context (active tenant set), so the direct read
    /// passes RLS: system roles have <c>tenant_id IS NULL</c> (globally visible) and the membership rows belong
    /// to the caller's own tenant. Never an authorization input — permissions come from resolve_user_permissions.
    /// </summary>
    public async Task<IReadOnlyList<UserRoleLabel>> GetRoleLabelsAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<RoleLabelRow>(
                """
                SELECT r.role_key AS "RoleKey", r.name AS "Name"
                FROM platform.user_tenant_roles utr
                JOIN platform.roles r ON r.role_id = utr.role_id
                WHERE utr.user_id = @p0 AND utr.tenant_id = @p1 AND utr.revoked_at IS NULL
                ORDER BY r.name
                """,
                new NpgsqlParameter("@p0", userId), new NpgsqlParameter("@p1", tenantId))
            .ToListAsync(ct);

        return rows.Select(r => new UserRoleLabel(r.RoleKey, r.Name)).ToList();
    }

    private sealed record RoleLabelRow(string RoleKey, string Name);

    /// <summary>
    /// Onboarding insert — platform.tenants carries no RLS, so a direct parameterised INSERT on the ambient
    /// UoW transaction is the sanctioned path (the caller is gated on <c>platform.tenants.create</c>). Status
    /// starts <c>active</c> so the owner can sign in the moment they accept their invitation; country/timezone/
    /// settings/regulatory_metadata come from the schema defaults.
    /// </summary>
    public async Task<Guid> CreateAsync(
        string tenantCode, string legalName, string displayName, string tenantType,
        string primaryEmail, string primaryPhone, string? city, string? state,
        string? pinCode, decimal? latitude, decimal? longitude, CancellationToken ct)
    {
        try
        {
            // The geo tag lives under settings.geo (JSONB) — platform.tenants has no coordinate
            // columns yet, and settings is the sanctioned per-tenant config bag. Both-or-neither is
            // enforced by the validator; the CASE keeps settings '{}' when untagged.
            var rows = await db.Database.SqlQueryRaw<Guid>(
                    """
                    INSERT INTO platform.tenants
                        (tenant_code, legal_name, display_name, tenant_type, primary_email, primary_phone,
                         city, state, pin_code, settings, status)
                    VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8,
                            CASE WHEN @p9::numeric IS NOT NULL AND @p10::numeric IS NOT NULL
                                 THEN jsonb_build_object('geo', jsonb_build_object(
                                          'latitude', @p9::numeric, 'longitude', @p10::numeric,
                                          'source', 'pincode_lookup', 'tagged_at', now()))
                                 -- argless jsonb_build_object() = empty object; a literal brace pair
                                 -- anywhere in this string breaks SqlQueryRaw placeholder parsing.
                                 ELSE jsonb_build_object() END,
                            'active')
                    RETURNING tenant_id AS "Value"
                    """,
                    new NpgsqlParameter("@p0", tenantCode),
                    new NpgsqlParameter("@p1", legalName),
                    new NpgsqlParameter("@p2", displayName),
                    new NpgsqlParameter("@p3", tenantType),
                    new NpgsqlParameter("@p4", primaryEmail),
                    new NpgsqlParameter("@p5", primaryPhone),
                    new NpgsqlParameter("@p6", (object?)city ?? DBNull.Value),
                    new NpgsqlParameter("@p7", (object?)state ?? DBNull.Value),
                    new NpgsqlParameter("@p8", (object?)pinCode ?? DBNull.Value),
                    new NpgsqlParameter("@p9", NpgsqlTypes.NpgsqlDbType.Numeric) { Value = (object?)latitude ?? DBNull.Value },
                    new NpgsqlParameter("@p10", NpgsqlTypes.NpgsqlDbType.Numeric) { Value = (object?)longitude ?? DBNull.Value })
                .ToListAsync(ct);
            return rows.First();
        }
        catch (PostgresException pg) when (pg.SqlState == "23505")
        {
            throw new ConflictException($"A tenant with code '{tenantCode}' already exists.", pg);
        }
    }
}
