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

/// <summary>One ATTRIBUTED effective permission for the "why does this user have X?" explainer — the same
/// resolved set as <see cref="EffectiveAccessDto"/> but with each key tagged by its <c>Source</c>
/// ('role' = via a role grant | 'override_grant' = via a per-user grant-override). <c>Via</c> (the granting
/// role name) is null: <c>platform.v_user_effective_permissions</c> does not carry role attribution.</summary>
public sealed record EffectivePermissionDto(string PermissionKey, string Source, string? Via);

/// <summary>A per-user permission OVERRIDE (deny-wins, time-boxable) shown in the user-management panel.
/// <c>IsAllowed</c> false = DENY (wins over a role grant), true = GRANT. Only currently-effective rows
/// (active, started, not expired) are returned; the granting reason + optional expiry are surfaced.</summary>
public sealed record UserPermissionOverrideDto(
    Guid OverrideId, string PermissionKey, bool IsAllowed, string Reason, DateTime? ExpiresAt);

/// <summary>One row of the tenant-wide "Per-user overrides" tab: a per-user override PLUS the target user's
/// identity (so the list renders without a second lookup). Every row is scoped to the CURRENT tenant (never
/// another tenant's, never a platform-wide NULL-tenant override). <c>Active</c> = currently effective
/// (started, not expired); <c>IsAllowed</c> false = DENY (deny-wins), true = GRANT.</summary>
public sealed record TenantPermissionOverrideDto(
    Guid OverrideId, Guid UserId, string UserDisplayName, string UserEmail,
    string PermissionKey, bool IsAllowed, string Reason,
    DateTime EffectiveFrom, DateTime? ExpiresAt, bool Active);

/// <summary>The tenant-wide overrides list plus a <c>Count</c> for the tab badge.</summary>
public sealed record TenantOverridesListDto(int Count, IReadOnlyList<TenantPermissionOverrideDto> Overrides);

// ---- Catalog plane (platform-governed): create modules + permissions ----------------------------
// The "vocabulary" of authority. Creating these is a platform-admin act gated on
// platform.permissions.manage. A permission is inert until application code checks it.

/// <summary>Create a module (resource_type) — a new privilege group in the matrix.</summary>
public sealed record CreateModuleRequest(string ResourceKey, string Name, string? Description, int DisplayOrder = 0);

public sealed record CreateModuleResult(Guid ResourceTypeId);

/// <summary>Create a permission (<c>resource.action</c>). It becomes grantable + visible in the matrix
/// immediately; enforcement (a <c>[RequirePermission]</c> check) ships with the feature that needs it.</summary>
public sealed record CreatePermissionRequest(
    string PermissionKey, string Resource, string Action, string Scope, string Description, bool IsDangerous = false);

public sealed record CreatePermissionResult(Guid PermissionId);

/// <summary>Set a tenant's per-module license (denylist; default-licensed). A COMMERCIAL DISPLAY gate —
/// it only greys cells in the matrix and never changes access. Gated on <c>platform.settings.update</c>.</summary>
public sealed record SetModuleLicenseRequest(Guid? TenantId, bool IsLicensed, string? Reason);

public sealed record SetModuleLicenseResult(Guid EntitlementId, Guid ResourceTypeId, bool IsLicensed);
