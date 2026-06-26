using mediq.SharedDataModel.Docslot.Iam;

namespace mediq.Application.Abstractions;

/// <summary>
/// Read side of the IAM (Roles &amp; permissions) admin surface. Projects the canonical RBAC catalog
/// (<c>platform.permissions</c> / <c>role_permissions</c> / <c>resource_types</c> / <c>action_types</c>)
/// into the matrix the "Team &amp; roles" screen renders. All resolution logic stays in the database; this
/// port only shapes rows. Writes never go through here — they use the SECURITY DEFINER functions.
/// </summary>
public interface IIamReadService
{
    /// <summary>Lists the modules (resource groups) used to head the privilege matrix. Active only, ordered.</summary>
    Task<IReadOnlyList<ModuleDto>> ListModulesAsync(CancellationToken ct);

    /// <summary>Lists permissions in the catalog, optionally narrowed to one module (resource key).</summary>
    Task<IReadOnlyList<PermissionDto>> ListPermissionsAsync(string? resourceKey, CancellationToken ct);

    /// <summary>Assembles the full grant matrix for a role (modules → action cells). Null if the role is unknown.</summary>
    Task<RoleMatrixDto?> GetRoleMatrixAsync(Guid roleId, CancellationToken ct);

    /// <summary>The effective permission set for a user in a tenant — delegates to <c>resolve_user_permissions</c>.</summary>
    Task<EffectiveAccessDto> GetEffectiveAccessAsync(Guid userId, Guid? tenantId, CancellationToken ct);
}
