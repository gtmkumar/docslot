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
