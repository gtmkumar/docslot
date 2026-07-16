using mediq.SharedDataModel.Docslot.Navigation;

namespace mediq.Application.Abstractions;

/// <summary>
/// Reads the canonical RBAC engine in PostgreSQL (database/08_rbac_navigation.sql) via
/// <c>FromSqlRaw</c>. The schema owns the resolution logic (deny-wins, time-boxed overrides,
/// tenant-type-aware menus); this port never reimplements it in C#.
/// </summary>
public interface IRbacQueryService
{
    /// <summary>Calls <c>platform.resolve_user_permissions(p_user_id, p_tenant_id)</c> — single query, effective set.</summary>
    Task<IReadOnlySet<string>> ResolvePermissionsAsync(Guid userId, Guid? tenantId, CancellationToken ct);

    /// <summary>Calls <c>platform.get_user_menus(...)</c> and assembles the flat rows into a bilingual tree.
    /// A null <paramref name="tenantId"/> is the PLATFORM scope (a super_admin with no active tenant): the
    /// function then returns only global menus, filtered by the caller's platform-level permission set.</summary>
    Task<IReadOnlyList<MenuNodeDto>> GetMenusAsync(
        Guid userId, Guid? tenantId, string? tenantType, string productKey, CancellationToken ct);

    /// <summary>Calls <c>platform.user_has_permission(...)</c>. Prefer the in-memory <see cref="IPermissionContext"/> for request-path checks.</summary>
    Task<bool> HasPermissionAsync(Guid userId, string permissionKey, Guid? tenantId, CancellationToken ct);
}
