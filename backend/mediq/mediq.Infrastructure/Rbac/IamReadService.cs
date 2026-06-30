using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.Iam;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Rbac;

/// <summary>
/// Projects the canonical RBAC catalog into the IAM admin read models. Reads run inside the request
/// transaction (RLS-scoped to own-tenant + global rows), so a tenant admin sees built-in/global roles and
/// their own custom roles, never another tenant's. Effective-access resolution is delegated to
/// <see cref="IRbacQueryService"/> so deny-wins / time-boxing logic lives only in the database.
/// </summary>
public sealed class IamReadService(PlatformDbContext db, IRbacQueryService rbac, ICurrentUserContext ctx) : IIamReadService
{
    // Canonical column order for the matrix when action_types.display_order is unset (the seed leaves it 0).
    private static readonly Dictionary<string, int> ActionRank = new(StringComparer.Ordinal)
    {
        ["read"] = 1, ["view"] = 1, ["create"] = 2, ["update"] = 3, ["edit"] = 3,
        ["approve"] = 4, ["delete"] = 5, ["export"] = 6,
    };

    private static int RankOf(string actionKey) => ActionRank.GetValueOrDefault(actionKey, 50);

    public async Task<IReadOnlyList<ModuleDto>> ListModulesAsync(CancellationToken ct)
    {
        var rows = await db.ResourceTypes.AsNoTracking()
            .Where(r => r.IsActive)
            .OrderBy(r => r.DisplayOrder).ThenBy(r => r.ResourceName)
            .ToListAsync(ct);

        // Licensing is per the caller's tenant (denylist — licensed unless explicitly disabled).
        var unlicensed = await UnlicensedModuleIdsAsync(ctx.TenantId, ct);

        return rows
            .Select(r => new ModuleDto(
                r.ResourceKey, r.ResourceName, r.Description, r.DisplayOrder,
                Licensed: !unlicensed.Contains(r.ResourceTypeId)))
            .ToList();
    }

    public async Task<IReadOnlyList<PermissionDto>> ListPermissionsAsync(string? resourceKey, CancellationToken ct)
    {
        var q = db.Permissions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(resourceKey))
            q = q.Where(p => p.Resource == resourceKey);

        var rows = await q.OrderBy(p => p.Resource).ThenBy(p => p.Action).ToListAsync(ct);

        return rows
            .Select(p => new PermissionDto(
                p.PermissionId, p.PermissionKey, p.Resource, p.Action, p.Scope, p.IsDangerous, p.Description))
            .ToList();
    }

    public async Task<RoleMatrixDto?> GetRoleMatrixAsync(Guid roleId, CancellationToken ct)
    {
        var role = await db.Roles.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RoleId == roleId && r.DeletedAt == null, ct);
        if (role is null) return null;

        // A role only ever holds permissions of its own scope band: platform roles → platform perms;
        // tenant roles → tenant + self perms. This keeps the matrix relevant and mirrors the SQL seeds.
        var inScope = role.Scope == "platform"
            ? new[] { "platform" }
            : ["tenant", "self"];

        var perms = await db.Permissions.AsNoTracking()
            .Where(p => inScope.Contains(p.Scope))
            .ToListAsync(ct);

        var grantedIds = (await db.RolePermissions.AsNoTracking()
                .Where(rp => rp.RoleId == roleId)
                .Select(rp => rp.PermissionId)
                .ToListAsync(ct))
            .ToHashSet();

        var moduleMeta = await db.ResourceTypes.AsNoTracking()
            .ToDictionaryAsync(r => r.ResourceKey, ct);
        var actionMeta = await db.ActionTypes.AsNoTracking()
            .ToDictionaryAsync(a => a.ActionKey, ct);

        // Licensing follows the role's tenant for a custom role, else the caller's tenant context.
        // It is a DISPLAY gate only — the Granted flags above are untouched by it.
        var unlicensed = await UnlicensedModuleIdsAsync(role.TenantId ?? ctx.TenantId, ct);

        var modules = perms
            .GroupBy(p => p.Resource)
            .Select(g =>
            {
                moduleMeta.TryGetValue(g.Key, out var rt);
                var licensed = rt is null || !unlicensed.Contains(rt.ResourceTypeId);
                var cells = g
                    .OrderBy(p => actionMeta.TryGetValue(p.Action, out var at) && at.DisplayOrder > 0
                        ? at.DisplayOrder : RankOf(p.Action))
                    .ThenBy(p => p.Action)
                    .Select(p => new RoleMatrixCellDto(
                        p.PermissionId, p.PermissionKey, p.Action,
                        actionMeta.TryGetValue(p.Action, out var at) ? at.ActionName : Humanize(p.Action),
                        p.IsDangerous, Granted: grantedIds.Contains(p.PermissionId), ModuleLicensed: licensed))
                    .ToList();

                return new RoleMatrixModuleDto(
                    g.Key,
                    rt?.ResourceName ?? Humanize(g.Key),
                    rt?.Description,
                    rt?.DisplayOrder ?? int.MaxValue,
                    Licensed: licensed,
                    GrantedCount: cells.Count(c => c.Granted),
                    TotalCount: cells.Count,
                    Cells: cells);
            })
            .OrderBy(m => m.DisplayOrder).ThenBy(m => m.Name)
            .ToList();

        return new RoleMatrixDto(
            role.RoleId, role.RoleKey, role.Name, role.Description, role.Scope, role.IsSystem,
            Editable: !role.IsSystem,
            GrantedCount: modules.Sum(m => m.GrantedCount),
            TotalCount: modules.Sum(m => m.TotalCount),
            Modules: modules);
    }

    public async Task<EffectiveAccessDto> GetEffectiveAccessAsync(Guid userId, Guid? tenantId, CancellationToken ct)
    {
        // resolve_user_permissions is SECURITY DEFINER (bypasses RLS) and filters ONLY by its tenant arg, which
        // is the (client-supplied) ?tenantId. Gate it on the SERVER-trusted context: a caller may only resolve a
        // tenant they can see (own / impersonated / super). Else return empty — never disclose another tenant.
        if (!await CanSeeTenantAsync(tenantId, ct))
            return new EffectiveAccessDto(userId, tenantId, []);
        var keys = await rbac.ResolvePermissionsAsync(userId, tenantId, ct);
        return new EffectiveAccessDto(userId, tenantId, keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    public async Task<IReadOnlyList<EffectivePermissionDto>> GetEffectivePermissionsAsync(Guid userId, Guid? tenantId, CancellationToken ct) =>
        // CRITICAL: v_user_effective_permissions is NOT a security_invoker view → it reads its base tables as the
        // (superuser) view owner and BYPASSES RLS. So the per-row platform.rls_can_see_tenant(tenant_id) guard is
        // the SOLE tenant boundary here (it uses the server-signed current_tenant_id()/super GUC, so a client
        // ?tenantId can only NARROW, never widen): a tenant-A caller passing ?tenantId=B gets back only the user's
        // global (tenant_id NULL) rows, never tenant-B rows; a super_admin context sees across tenants by design.
        await db.Database.SqlQueryRaw<EffectivePermissionDto>(
                """
                SELECT permission_key AS "PermissionKey", source AS "Source", NULL::text AS "Via"
                FROM platform.v_user_effective_permissions
                WHERE user_id = @p_user
                  AND (tenant_id = @p_tenant::uuid OR tenant_id IS NULL)
                  AND platform.rls_can_see_tenant(tenant_id)
                ORDER BY permission_key
                """,
                new NpgsqlParameter("@p_user", userId),
                new NpgsqlParameter("@p_tenant", (object?)tenantId ?? DBNull.Value))
            .ToListAsync(ct);

    /// <summary>Server-trusted "can the CALLER see this tenant?" check — <c>platform.rls_can_see_tenant</c> reads
    /// the signed GUCs (current_tenant_id / impersonation / super), so a client-supplied tenant id can never
    /// widen authority. A null tenant is platform scope (resolve returns only platform rows) → allowed.</summary>
    private async Task<bool> CanSeeTenantAsync(Guid? tenantId, CancellationToken ct)
    {
        if (tenantId is null) return true;
        var rows = await db.Database.SqlQueryRaw<VisibilityRow>(
                "SELECT COALESCE(platform.rls_can_see_tenant(@t::uuid), false) AS \"CanSee\"",
                new NpgsqlParameter("@t", tenantId.Value))
            .ToListAsync(ct);
        return rows.FirstOrDefault()?.CanSee ?? false;
    }

    private sealed record VisibilityRow(bool CanSee);

    public async Task<IReadOnlyList<UserPermissionOverrideDto>> ListUserOverridesAsync(Guid userId, Guid? tenantId, CancellationToken ct) =>
        // Only CURRENTLY-EFFECTIVE overrides (active, started, not expired). Plain request-tx read → the
        // upo_read RLS policy (rls_can_see_tenant) scopes it; a tenant-scoped override of another tenant is
        // invisible. Matches a tenant-scoped (or NULL platform-wide) override that affects @p_tenant.
        await db.Database.SqlQueryRaw<UserPermissionOverrideDto>(
                """
                SELECT o.override_id AS "OverrideId", p.permission_key AS "PermissionKey", o.is_allowed AS "IsAllowed",
                       o.reason AS "Reason", o.expires_at AS "ExpiresAt"
                FROM platform.user_permission_overrides o
                JOIN platform.permissions p ON p.permission_id = o.permission_id
                WHERE o.user_id = @p_user
                  AND o.is_active = true
                  AND o.effective_from <= NOW()
                  AND (o.expires_at IS NULL OR o.expires_at > NOW())
                  AND (@p_tenant::uuid IS NULL OR o.tenant_id = @p_tenant::uuid OR o.tenant_id IS NULL)
                ORDER BY p.permission_key
                """,
                new NpgsqlParameter("@p_user", userId),
                new NpgsqlParameter("@p_tenant", (object?)tenantId ?? DBNull.Value))
            .ToListAsync(ct);

    /// <summary>The set of module (resource_type) ids the tenant has explicitly UN-licensed (denylist).
    /// Empty when there's no tenant context — modules then render as licensed by default.</summary>
    private async Task<HashSet<Guid>> UnlicensedModuleIdsAsync(Guid? tenantId, CancellationToken ct)
    {
        if (tenantId is null) return [];
        var ids = await db.TenantModuleEntitlements.AsNoTracking()
            .Where(e => e.TenantId == tenantId && !e.IsLicensed)
            .Select(e => e.ResourceTypeId)
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    private static string Humanize(string key) =>
        string.Join(' ', key.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}
