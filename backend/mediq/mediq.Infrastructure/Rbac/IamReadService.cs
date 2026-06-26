using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.Iam;
using Microsoft.EntityFrameworkCore;

namespace mediq.Infrastructure.Rbac;

/// <summary>
/// Projects the canonical RBAC catalog into the IAM admin read models. Reads run inside the request
/// transaction (RLS-scoped to own-tenant + global rows), so a tenant admin sees built-in/global roles and
/// their own custom roles, never another tenant's. Effective-access resolution is delegated to
/// <see cref="IRbacQueryService"/> so deny-wins / time-boxing logic lives only in the database.
/// </summary>
public sealed class IamReadService(PlatformDbContext db, IRbacQueryService rbac) : IIamReadService
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

        return rows
            .Select(r => new ModuleDto(r.ResourceKey, r.ResourceName, r.Description, r.DisplayOrder, Licensed: true))
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

        var modules = perms
            .GroupBy(p => p.Resource)
            .Select(g =>
            {
                moduleMeta.TryGetValue(g.Key, out var rt);
                var cells = g
                    .OrderBy(p => actionMeta.TryGetValue(p.Action, out var at) && at.DisplayOrder > 0
                        ? at.DisplayOrder : RankOf(p.Action))
                    .ThenBy(p => p.Action)
                    .Select(p => new RoleMatrixCellDto(
                        p.PermissionId, p.PermissionKey, p.Action,
                        actionMeta.TryGetValue(p.Action, out var at) ? at.ActionName : Humanize(p.Action),
                        p.IsDangerous, Granted: grantedIds.Contains(p.PermissionId), ModuleLicensed: true))
                    .ToList();

                return new RoleMatrixModuleDto(
                    g.Key,
                    rt?.ResourceName ?? Humanize(g.Key),
                    rt?.Description,
                    rt?.DisplayOrder ?? int.MaxValue,
                    Licensed: true,
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
        var keys = await rbac.ResolvePermissionsAsync(userId, tenantId, ct);
        return new EffectiveAccessDto(userId, tenantId, keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    private static string Humanize(string key) =>
        string.Join(' ', key.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}
