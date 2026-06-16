using mediq.Application.Abstractions;
using mediq.Infrastructure.Persistence;
using mediq.SharedDataModel.Docslot.Navigation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace mediq.Infrastructure.Rbac;

/// <summary>
/// Invokes the canonical RBAC engine (database/08_rbac_navigation.sql) via <c>FromSqlRaw</c> /
/// <c>SqlQueryRaw</c>. The schema owns deny-wins, time-boxed overrides, and tenant-type-aware menu
/// filtering; this service never reimplements that logic — it only marshals parameters and assembles
/// the flat menu rows into the bilingual tree the frontend expects.
/// </summary>
public sealed class RbacQueryService(PlatformDbContext db) : IRbacQueryService
{
    public async Task<IReadOnlySet<string>> ResolvePermissionsAsync(Guid userId, Guid? tenantId, CancellationToken ct)
    {
        var rows = await db.PermissionKeyRows
            .FromSqlRaw(
                "SELECT permission_key AS \"PermissionKey\" FROM platform.resolve_user_permissions(@p0, @p1)",
                new NpgsqlParameter("@p0", userId),
                new NpgsqlParameter("@p1", (object?)tenantId ?? DBNull.Value))
            .AsNoTracking()
            .ToListAsync(ct);

        return rows.Select(r => r.PermissionKey).ToHashSet(StringComparer.Ordinal);
    }

    public async Task<bool> HasPermissionAsync(Guid userId, string permissionKey, Guid? tenantId, CancellationToken ct)
    {
        var rows = await db.BoolRows
            .FromSqlRaw(
                "SELECT platform.user_has_permission(@p0, @p1, @p2) AS \"Value\"",
                new NpgsqlParameter("@p0", userId),
                new NpgsqlParameter("@p1", permissionKey),
                new NpgsqlParameter("@p2", (object?)tenantId ?? DBNull.Value))
            .AsNoTracking()
            .ToListAsync(ct);

        return rows.FirstOrDefault()?.Value ?? false;
    }

    public async Task<IReadOnlyList<MenuNodeDto>> GetMenusAsync(
        Guid userId, Guid tenantId, string? tenantType, string productKey, CancellationToken ct)
    {
        var flat = await db.MenuRows
            .FromSqlRaw(
                """
                SELECT menu_id AS "MenuId", parent_menu_id AS "ParentMenuId", menu_key AS "MenuKey",
                       menu_label AS "MenuLabel", menu_label_hi AS "MenuLabelHi", menu_icon AS "MenuIcon",
                       menu_url AS "MenuUrl", display_order AS "DisplayOrder",
                       is_section_header AS "IsSectionHeader", badge_source AS "BadgeSource"
                FROM platform.get_user_menus(@p0, @p1, @p2, @p3)
                """,
                new NpgsqlParameter("@p0", userId),
                new NpgsqlParameter("@p1", tenantId),
                new NpgsqlParameter("@p2", (object?)tenantType ?? DBNull.Value),
                new NpgsqlParameter("@p3", productKey))
            .AsNoTracking()
            .ToListAsync(ct);

        return BuildTree(flat);
    }

    /// <summary>Assembles the ordered flat rows into a parent→children tree (matches MenuNodeDto contract).</summary>
    private static IReadOnlyList<MenuNodeDto> BuildTree(IReadOnlyList<MenuRow> rows)
    {
        var childrenByParent = rows
            .GroupBy(r => r.ParentMenuId)
            .ToDictionary(g => g.Key ?? Guid.Empty, g => g.OrderBy(r => r.DisplayOrder).ToList());

        List<MenuNodeDto> Build(Guid parentKey) =>
            (childrenByParent.TryGetValue(parentKey, out var kids) ? kids : [])
            .Select(r => new MenuNodeDto(
                r.MenuId, r.ParentMenuId, r.MenuKey, r.MenuLabel, r.MenuLabelHi, r.MenuIcon, r.MenuUrl,
                r.DisplayOrder, r.IsSectionHeader, r.BadgeSource, Build(r.MenuId)))
            .ToList();

        return Build(Guid.Empty);   // roots have parent_menu_id = NULL → keyed as Guid.Empty
    }
}
