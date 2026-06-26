namespace mediq.SharedDataModel.Docslot.Iam;

// ============================================================================
// IAM — Roles & permissions admin surface (the "Team & roles" screen).
// Read models project the canonical RBAC tables (platform.permissions /
// role_permissions / resource_types / action_types); writes go through the
// SECURITY DEFINER functions in database/11_rbac_hardening.sql.
// ============================================================================

/// <summary>A module = a privilege group (maps to <c>platform.resource_types</c>, i.e. the resource the
/// permission acts on). The licensing flag is a forward seam; until per-module entitlement ships it is
/// always <c>true</c> and must never be treated as a security boundary (RBAC is).</summary>
public sealed record ModuleDto(
    string ResourceKey, string Name, string? Description, int DisplayOrder, bool Licensed = true);

/// <summary>An action = a matrix column (maps to <c>platform.action_types</c>).</summary>
public sealed record ActionDto(string ActionKey, string Name, bool IsDangerous, int DisplayOrder);

/// <summary>A single permission in the catalog (maps to <c>platform.permissions</c>).</summary>
public sealed record PermissionDto(
    Guid PermissionId, string PermissionKey, string Resource, string Action, string Scope,
    bool IsDangerous, string Description);

/// <summary>One cell of the role matrix: a permission and whether this role grants it.</summary>
public sealed record RoleMatrixCellDto(
    Guid PermissionId, string PermissionKey, string Action, string ActionName,
    bool IsDangerous, bool Granted, bool ModuleLicensed = true);

/// <summary>One module row of the role matrix: its label and the action cells under it.</summary>
public sealed record RoleMatrixModuleDto(
    string ResourceKey, string Name, string? Description, int DisplayOrder, bool Licensed,
    int GrantedCount, int TotalCount, IReadOnlyList<RoleMatrixCellDto> Cells);

/// <summary>The full privilege matrix for a role — the heart of the screen. <c>Editable</c> is a UI hint
/// (false for built-in roles); the real write authority is re-checked server-side by the definer funcs.</summary>
public sealed record RoleMatrixDto(
    Guid RoleId, string RoleKey, string Name, string? Description, string Scope, bool IsSystem,
    bool Editable, int GrantedCount, int TotalCount, IReadOnlyList<RoleMatrixModuleDto> Modules);

/// <summary>Duplicate an existing role into a new custom role, copying its grants.</summary>
public sealed record DuplicateRoleRequest(
    Guid SourceRoleId, string NewRoleKey, string NewName, string? Description, Guid? TenantId);

public sealed record DuplicateRoleResult(Guid RoleId);

/// <summary>Grant or revoke a single permission on a role (the matrix checkbox toggle).</summary>
public sealed record SetRolePermissionRequest(Guid? TenantId, bool Grantable = false);

public sealed record SetRolePermissionResult(Guid RoleId, Guid PermissionId, bool Granted);

/// <summary>The effective (resolved) permission set for a user in a tenant — role grants minus
/// deny-overrides plus grant-overrides, exactly as <c>platform.resolve_user_permissions</c> computes it.</summary>
public sealed record EffectiveAccessDto(
    Guid UserId, Guid? TenantId, IReadOnlyList<string> PermissionKeys);
