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

    /// <summary>
    /// Edit path — platform.tenants carries no RLS, so a direct parameterised UPDATE on the ambient UoW
    /// transaction is the sanctioned path (the caller is gated on <c>platform.tenants.update</c>). tenant_code,
    /// tenant_type AND status are intentionally NOT in the SET list — identity/structure are immutable here and
    /// status is owned by <see cref="SetStatusAsync"/> (the suspend path). updated_at is left to the
    /// <c>trg_tenants_updated_at</c> BEFORE UPDATE trigger. The <c>deleted_at IS NULL</c> guard means a
    /// soft-deleted tenant matches nothing → returns false (the handler already 404s on a prior GetByIdAsync).
    /// </summary>
    public async Task<bool> UpdateAsync(
        Guid tenantId, string displayName, string legalName, string primaryEmail, string primaryPhone,
        string? city, string? state, string? pinCode, decimal? latitude, decimal? longitude, CancellationToken ct)
    {
        try
        {
            // Geo re-tag reuses CreateAsync's settings.geo shape, MERGED into the existing settings (|| preserves
            // other keys). When either coordinate is null the CASE keeps settings unchanged, so a contact-only edit
            // never wipes an existing geo tag. jsonb_build_object (no literal braces) keeps SqlRaw placeholder
            // parsing happy — same reason CreateAsync avoids '{}'::jsonb.
            var affected = await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE platform.tenants
                SET display_name  = @p1,
                    legal_name    = @p2,
                    primary_email = @p3,
                    primary_phone = @p4,
                    city          = @p5,
                    state         = @p6,
                    pin_code      = @p7,
                    settings      = CASE WHEN @p8::numeric IS NOT NULL AND @p9::numeric IS NOT NULL
                                         THEN settings || jsonb_build_object('geo', jsonb_build_object(
                                                  'latitude', @p8::numeric, 'longitude', @p9::numeric,
                                                  'source', 'pincode_lookup', 'tagged_at', now()))
                                         ELSE settings END
                WHERE tenant_id = @p0 AND deleted_at IS NULL
                """,
                [
                    new NpgsqlParameter("@p0", tenantId),
                    new NpgsqlParameter("@p1", displayName),
                    new NpgsqlParameter("@p2", legalName),
                    new NpgsqlParameter("@p3", primaryEmail),
                    new NpgsqlParameter("@p4", primaryPhone),
                    new NpgsqlParameter("@p5", (object?)city ?? DBNull.Value),
                    new NpgsqlParameter("@p6", (object?)state ?? DBNull.Value),
                    new NpgsqlParameter("@p7", (object?)pinCode ?? DBNull.Value),
                    new NpgsqlParameter("@p8", NpgsqlTypes.NpgsqlDbType.Numeric) { Value = (object?)latitude ?? DBNull.Value },
                    new NpgsqlParameter("@p9", NpgsqlTypes.NpgsqlDbType.Numeric) { Value = (object?)longitude ?? DBNull.Value },
                ],
                ct);
            return affected > 0;
        }
        catch (PostgresException pg) when (pg.SqlState == "23505")
        {
            throw new ConflictException("A tenant with these details already exists.", pg);
        }
    }

    /// <summary>
    /// Suspend / reactivate — the ONLY write path for <c>status</c> (caller gated on the DANGEROUS
    /// <c>platform.tenants.suspend</c>). Sets status and <c>suspended_reason</c> atomically: the mandatory reason on
    /// suspend, NULL on reactivate. Direct parameterised UPDATE on the UoW transaction (no RLS on platform.tenants);
    /// updated_at is left to the trigger. Returns false when no LIVE row matched.
    /// </summary>
    public async Task<bool> SetStatusAsync(
        Guid tenantId, string status, string? suspendedReason, CancellationToken ct)
    {
        var affected = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE platform.tenants
            SET status           = @p1,
                suspended_reason = @p2
            WHERE tenant_id = @p0 AND deleted_at IS NULL
            """,
            [
                new NpgsqlParameter("@p0", tenantId),
                new NpgsqlParameter("@p1", status),
                new NpgsqlParameter("@p2", (object?)suspendedReason ?? DBNull.Value),
            ],
            ct);
        return affected > 0;
    }
}
