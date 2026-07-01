namespace mediq.Domain.Platform;

/// <summary>Job-specific permission bundle (maps to <c>platform.roles</c>).</summary>
public sealed class Role
{
    public Guid RoleId { get; private set; }
    public string RoleKey { get; private set; } = default!;   // 'super_admin','tenant_owner','doctor',...
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }
    public Guid? TenantId { get; private set; }               // null for system roles
    public string Scope { get; private set; } = "tenant";     // 'platform' | 'tenant'
    public bool IsSystem { get; private set; }
    public bool IsDefault { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? DeletedAt { get; private set; }

    private Role() { }

    /// <summary>
    /// Creates a CUSTOM role. System roles (super_admin, tenant_owner, …) are seeded in SQL and are NOT
    /// created here — <see cref="IsSystem"/> is always false. A tenant-scoped role MUST carry a tenant id
    /// (also enforced by the table's CHECK constraint).
    /// </summary>
    public static Role CreateCustom(
        string roleKey, string name, string? description, Guid? tenantId, string scope, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(roleKey)) throw new ArgumentException("Role key is required.", nameof(roleKey));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Role name is required.", nameof(name));
        if (scope is not ("tenant" or "platform")) throw new ArgumentException("Scope must be 'tenant' or 'platform'.", nameof(scope));
        if (scope == "tenant" && tenantId is null) throw new ArgumentException("A tenant-scoped role requires a tenant id.", nameof(tenantId));

        return new Role
        {
            RoleId = Guid.CreateVersion7(),
            RoleKey = roleKey,
            Name = name,
            Description = description,
            TenantId = tenantId,
            Scope = scope,
            IsSystem = false,
            IsDefault = false,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        };
    }
}

/// <summary>
/// Bridge assigning a role to a user within a tenant (maps to <c>platform.user_tenant_roles</c>).
/// The heart of access control. tenant_id is null for platform-level assignments (super_admin).
/// </summary>
public sealed class UserTenantRole
{
    public Guid UserTenantRoleId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid? TenantId { get; private set; }
    public Guid RoleId { get; private set; }
    public bool IsPrimary { get; private set; }
    public DateTime GrantedAt { get; private set; }
    public Guid? GrantedBy { get; private set; }
    public DateTime? ExpiresAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public Guid? RevokedBy { get; private set; }
    public string? RevokedReason { get; private set; }

    /// <summary>Organizational scope of this membership — DISPLAY ONLY. NULL branch = "All branches",
    /// NULL department = "All departments". Never read by permission resolution (see set_membership_scope).</summary>
    public Guid? BranchId { get; private set; }
    public string? Department { get; private set; }

    private UserTenantRole() { }

    public static UserTenantRole Assign(
        Guid userId, Guid? tenantId, Guid roleId, Guid? grantedBy, DateTime nowUtc,
        DateTime? expiresAt, bool isPrimary)
        => new()
        {
            UserTenantRoleId = Guid.CreateVersion7(),
            UserId = userId,
            TenantId = tenantId,
            RoleId = roleId,
            GrantedBy = grantedBy,
            GrantedAt = nowUtc,
            ExpiresAt = expiresAt,
            IsPrimary = isPrimary,
        };

    public void Revoke(Guid? revokedBy, string reason, DateTime nowUtc)
    {
        RevokedAt = nowUtc;
        RevokedBy = revokedBy;
        RevokedReason = reason;
    }

    public bool IsActive(DateTime nowUtc) =>
        RevokedAt is null && (ExpiresAt is null || ExpiresAt > nowUtc);
}

/// <summary>
/// Per-user grant/deny on top of role permissions (maps to <c>platform.user_permission_overrides</c>).
/// DENY wins. Every override requires a reason. Time-boxable.
/// </summary>
public sealed class UserPermissionOverride
{
    public Guid OverrideId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid PermissionId { get; private set; }
    public Guid? TenantId { get; private set; }
    public bool IsAllowed { get; private set; }              // true=GRANT, false=DENY (deny wins)
    public string Reason { get; private set; } = default!;
    public Guid? GrantedByUserId { get; private set; }
    public DateTime EffectiveFrom { get; private set; }
    public DateTime? ExpiresAt { get; private set; }
    public bool IsActiveFlag { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private UserPermissionOverride() { }

    public static UserPermissionOverride Create(
        Guid userId, Guid permissionId, Guid? tenantId, bool isAllowed, string reason,
        Guid? grantedBy, DateTime nowUtc, DateTime? expiresAt)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("An override reason is mandatory.", nameof(reason));

        return new UserPermissionOverride
        {
            OverrideId = Guid.CreateVersion7(),
            UserId = userId,
            PermissionId = permissionId,
            TenantId = tenantId,
            IsAllowed = isAllowed,
            Reason = reason,
            GrantedByUserId = grantedBy,
            EffectiveFrom = nowUtc,
            ExpiresAt = expiresAt,
            IsActiveFlag = true,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        };
    }
}

/// <summary>Granular permission registry row (maps to <c>platform.permissions</c>).</summary>
public sealed class Permission
{
    public Guid PermissionId { get; private set; }
    public string PermissionKey { get; private set; } = default!;
    public string Resource { get; private set; } = default!;
    public string Action { get; private set; } = default!;
    public string Scope { get; private set; } = "tenant";
    public string Description { get; private set; } = default!;
    public bool IsDangerous { get; private set; }

    private Permission() { }
}

/// <summary>
/// Role↔permission grant (maps to <c>platform.role_permissions</c>). Read-only here — every write goes
/// through the SECURITY DEFINER functions (grant/revoke_permission_from_role), never EF, because RLS on
/// the table blocks the app role from direct INSERT/DELETE.
/// </summary>
public sealed class RolePermission
{
    public Guid RoleId { get; private set; }
    public Guid PermissionId { get; private set; }
    public bool IsGrantable { get; private set; }
    public DateTime GrantedAt { get; private set; }

    private RolePermission() { }
}

/// <summary>
/// Module registry — the "what" a permission acts on (maps to <c>platform.resource_types</c>). Used to
/// label and order the privilege-matrix groups. A catalog artifact: read-only in the app.
/// </summary>
public sealed class ResourceType
{
    public Guid ResourceTypeId { get; private set; }
    public string ResourceKey { get; private set; } = default!;   // 'booking','patient','doctor',...
    public string ResourceName { get; private set; } = default!;  // 'Booking','Patient'
    public Guid? ProductId { get; private set; }
    public string? Description { get; private set; }
    public int DisplayOrder { get; private set; }
    public bool IsActive { get; private set; }

    private ResourceType() { }
}

/// <summary>
/// Action registry — the "what can be done" (maps to <c>platform.action_types</c>). Provides the matrix
/// column labels and the inherited dangerous flag. Catalog artifact: read-only in the app.
/// </summary>
public sealed class ActionType
{
    public Guid ActionTypeId { get; private set; }
    public string ActionKey { get; private set; } = default!;     // 'create','read','update','delete','approve','export'
    public string ActionName { get; private set; } = default!;
    public string? Description { get; private set; }
    public bool IsDangerous { get; private set; }
    public int DisplayOrder { get; private set; }
    public bool IsActive { get; private set; }

    private ActionType() { }
}

/// <summary>
/// Per-tenant per-module license (maps to <c>platform.tenant_module_entitlements</c>). DENYLIST: a module
/// is licensed unless a row marks it <c>is_licensed=false</c>. Read-only here — writes go through
/// <c>set_module_license</c>. A COMMERCIAL DISPLAY GATE ONLY: it greys cells in the matrix and never
/// affects permission resolution. See [[set_module_license]].
/// </summary>
public sealed class TenantModuleEntitlement
{
    public Guid EntitlementId { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ResourceTypeId { get; private set; }
    public bool IsLicensed { get; private set; }
    public string? Reason { get; private set; }

    private TenantModuleEntitlement() { }
}
