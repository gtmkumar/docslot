namespace mediq.SharedDataModel.Docslot.Admin;

/// <summary>Tenant summary for the platform admin LIST surface (maps to <c>platform.tenants</c>). Deliberately
/// lean — the directory grid never needs the full editable shape.</summary>
public sealed record TenantDto(
    Guid TenantId, string TenantCode, string DisplayName, string TenantType,
    string PrimaryEmail, string Status, string Country, string? City);

/// <summary>Full editable tenant shape for the DETAIL surface (GET /tenants/{id}, the edit PUT response, and the
/// suspend/reactivate responses). A superset of <see cref="TenantDto"/> carrying every field the edit form
/// pre-fills — <c>LegalName</c>, <c>PrimaryPhone</c>, <c>State</c>, <c>PinCode</c> — so the panel never opens fields
/// blank. <c>TenantCode</c>/<c>TenantType</c> are included for read-only display; they are NEVER editable.
/// <c>SuspendedReason</c> surfaces WHY a suspended clinic is suspended (NULL when active). Geo lat/long
/// (settings.geo) is intentionally omitted — the edit form doesn't surface coordinates.</summary>
public sealed record TenantDetailDto(
    Guid TenantId, string TenantCode, string DisplayName, string TenantType,
    string LegalName, string PrimaryEmail, string PrimaryPhone,
    string Status, string Country, string? City, string? State, string? PinCode,
    string? SuspendedReason);

/// <summary>Onboard a new tenant (clinic/hospital/lab) from the platform console. <c>AdminEmail</c> is the
/// initial Tenant Owner — a password is never involved: the command mints a <c>tenant_owner</c> invitation
/// and the owner sets their own credential on accept. Gated on <c>platform.tenants.create</c>.
/// <c>Latitude</c>/<c>Longitude</c> geo-tag the facility (typically from the PIN-code lookup) and are
/// stored under <c>settings.geo</c>; <c>PinCode</c> lands in the tenants.pin_code column.</summary>
public sealed record CreateTenantRequest(
    string TenantCode, string LegalName, string DisplayName, string TenantType,
    string PrimaryEmail, string PrimaryPhone, string? City, string? State,
    string? PinCode, decimal? Latitude, decimal? Longitude, string AdminEmail);

/// <summary>Result of onboarding a tenant. <c>InviteToken</c> is the ONE-TIME plaintext owner-invitation
/// token — surfaced exactly once (the response is never idempotency-cached); only its hash is persisted.</summary>
public sealed record CreateTenantResult(
    Guid TenantId, string TenantCode, string DisplayName,
    Guid InvitationId, string InviteToken, DateTime InviteExpiresAt, string AdminEmail);

/// <summary>Edit a tenant's mutable attributes from the platform console. Gated on
/// <c>platform.tenants.update</c>. <c>TenantCode</c> (identity) and <c>TenantType</c> (structural) are
/// deliberately absent — they are NEVER mutable here. <c>Status</c> is NOT editable through this path either:
/// suspend/reactivate is a distinct DANGEROUS action with its own permission and mandatory reason — see
/// <see cref="SetTenantStatusRequest"/>.</summary>
public sealed record UpdateTenantRequest(
    string DisplayName, string LegalName, string PrimaryEmail, string PrimaryPhone,
    string? City, string? State, string? PinCode);

/// <summary>Body for the DANGEROUS tenant suspend/reactivate endpoints (mirrors the broker
/// <c>SetBrokerStatusReasonRequest</c>). The transition is implied by the ROUTE
/// (<c>/tenants/{id}/suspend</c> vs <c>/tenants/{id}/reactivate</c>), each gated on
/// <c>platform.tenants.suspend</c>. <c>Reason</c> is MANDATORY on suspend (persisted to
/// <c>tenants.suspended_reason</c>) and ignored on reactivate (which clears it).</summary>
public sealed record SetTenantStatusReasonRequest(string? Reason);

/// <summary>A role a user holds in the tenant (for the user-row role chips). Carries the assignment id so
/// the manage panel can revoke without a second lookup.</summary>
public sealed record UserRoleDto(
    Guid UserTenantRoleId, Guid RoleId, string RoleKey, string Name, bool IsPrimary, DateTime? ExpiresAt);

/// <summary>User summary within a tenant. <c>IsActive</c> reflects TENANT activity (the user has at least one
/// active membership here), not the global <c>platform.users.is_active</c> flag. PHI: the phone is MASKED
/// server-side (raw phone never crosses the wire in an aggregate). <c>LockedUntil</c>/<c>MustChangePassword</c>
/// surface the account's security posture in the manage panel. <c>LastActivityAt</c> is the most-recent active
/// session's last_activity_at (issue #87) — drives the People tab "Online" dot; null when no live session.</summary>
public sealed record UserListItemDto(
    Guid UserId, string Email, string FullName, string? MaskedPhone, bool IsActive, bool MfaEnabled,
    DateTime? LastLoginAt, DateTime? LockedUntil, bool MustChangePassword,
    IReadOnlyList<UserRoleDto> Roles, DateTime? LastActivityAt = null,
    Guid? BranchId = null, string? BranchName = null, string? Department = null);

/// <summary>A tenant's physical branch/location (maps to <c>platform.branches</c>). An organizational display
/// attribute only — it heads the People "All branches" filter and never confers permissions.</summary>
public sealed record BranchDto(Guid BranchId, string Name, string? Code, bool IsActive);

/// <summary>Create a branch under the caller's tenant. Gated on <c>tenant.settings.update</c>.</summary>
public sealed record CreateBranchRequest(string Name, string? Code);

public sealed record CreateBranchResult(Guid BranchId);

/// <summary>Set a member's organizational scope — DISPLAY ONLY. NULL <c>BranchId</c> = "All branches",
/// NULL/blank <c>Department</c> = "All departments". Routed through <c>platform.set_membership_scope</c>,
/// which writes ONLY branch_id/department (never role_id) so it can never change effective access.</summary>
public sealed record SetMemberScopeRequest(Guid? BranchId, string? Department);

public sealed record SetMemberScopeResult(Guid UserTenantRoleId, Guid? BranchId, string? Department);

/// <summary>Create-user request. A password is deliberately NOT accepted from the admin (impersonation
/// hazard) — the invite seeds a server-generated temp credential + must-change-password.</summary>
public sealed record CreateUserRequest(
    string Email, string FullName, string? Phone,
    string PreferredLanguage = "en", Guid? InitialRoleId = null);

/// <summary><c>AlreadyExisted</c>=true when the email matched an existing global identity and we only linked a
/// new tenant membership (never overwriting the existing user's profile).</summary>
public sealed record CreateUserResult(Guid UserId, bool AlreadyExisted = false);

/// <summary>Deactivate (revoke the user's memberships in this tenant) or reactivate (restore them). A reason is
/// mandatory when deactivating. Tenant-scoped — never flips the global users.is_active.</summary>
public sealed record SetUserStatusRequest(bool IsActive, string Reason);

public sealed record SetUserStatusResult(Guid UserId, bool IsActive);

/// <summary>Edit a user's profile (whitelisted fields only). Email/auth/status are never mutable here.</summary>
public sealed record UpdateUserProfileRequest(string FullName, string? Phone, string PreferredLanguage = "en");

public sealed record UpdateUserProfileResult(Guid UserId);

/// <summary>Force a password change + clear the lockout (flags only; no plaintext). A reason is mandatory.</summary>
public sealed record ResetAccessRequest(string Reason);

public sealed record ResetAccessResult(Guid UserId);

/// <summary>Role summary (maps to <c>platform.roles</c>). <c>MemberCount</c> = distinct users holding this role
/// with an ACTIVE assignment (revoked_at IS NULL AND not expired) in the resolved tenant scope. For a system
/// (cross-tenant) role it counts only members within the current tenant, consistent with how the list is scoped.</summary>
public sealed record RoleDto(Guid RoleId, string RoleKey, string Name, string Scope, bool IsSystem, Guid? TenantId,
    int MemberCount = 0);

/// <summary>Create a custom (tenant-scoped) role. System roles are seeded in SQL and cannot be created here.</summary>
public sealed record CreateRoleRequest(
    string RoleKey, string Name, string? Description, Guid? TenantId, string Scope = "tenant");

public sealed record CreateRoleResult(Guid RoleId);

/// <summary>Assign a role to a user within a tenant, optionally time-boxed.</summary>
public sealed record AssignRoleRequest(Guid UserId, Guid RoleId, Guid? TenantId, DateTime? ExpiresAt, bool IsPrimary = false);

public sealed record AssignRoleResult(Guid UserTenantRoleId);

/// <summary>Revoke an existing role assignment (soft — sets revoked_at/by/reason; never deletes the row).</summary>
public sealed record RevokeRoleRequest(Guid UserTenantRoleId, string Reason);

public sealed record RevokeRoleResult(Guid UserTenantRoleId, bool AlreadyRevoked);

/// <summary>Grant or deny a single permission to a user (override). Reason is mandatory; deny wins.</summary>
public sealed record SetOverrideRequest(
    Guid UserId, string PermissionKey, bool IsAllowed, string Reason, Guid? TenantId, DateTime? ExpiresAt);

public sealed record SetOverrideResult(Guid OverrideId);
