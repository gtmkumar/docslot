using mediq.SharedDataModel.Docslot.Admin;

namespace mediq.Application.Abstractions;

/// <summary>
/// Read-side directory for listing users by tenant. Projects straight off the DbContext (CQRS read
/// trade-off) — no aggregate loading, so it does NOT go through <see cref="IUserRepository"/>.
/// </summary>
public interface IUserDirectory
{
    Task<IReadOnlyList<UserListItemDto>> ListByTenantAsync(Guid tenantId, int skip, int take, CancellationToken ct);
}

/// <summary>
/// Write-side user provisioning — creates the <c>platform.users</c> row only (the initial-role assignment is
/// orchestrated by the command handler through the escalation-safe definer path, NOT here). Seeds a server-
/// generated temp credential + must-change-password so <c>chk_user_has_auth</c> holds and no admin-known
/// password exists. On an email collision (users are a GLOBAL identity) it reuses the existing user_id and
/// returns <c>AlreadyExisted=true</c> without overwriting that user's profile.
/// </summary>
public interface IUserProvisioning
{
    Task<(Guid UserId, bool AlreadyExisted)> CreateAsync(CreateUserRequest request, DateTime nowUtc, CancellationToken ct);
}

/// <summary>
/// Write-side user-lifecycle operations, all routed through SECURITY DEFINER functions that re-check the
/// actor's permission, scope the target to the tenant, self-guard, and enforce the last-admin guard. The
/// actor is ALWAYS the authenticated principal. SQLSTATE 42501 → 403, 23xxx → 409, no_data_found (P0002,
/// target not a member) → 403 (avoids a 403/404 membership-enumeration oracle).
/// </summary>
public interface IUserLifecycle
{
    /// <summary>Deactivate (revoke memberships in the tenant) or reactivate (restore them).</summary>
    Task SetActiveAsync(Guid actorUserId, Guid targetUserId, Guid tenantId, bool isActive, string reason, CancellationToken ct);

    /// <summary>Edit full_name / phone / preferred_language only.</summary>
    Task UpdateProfileAsync(Guid actorUserId, Guid targetUserId, Guid tenantId, string fullName, string? phone, string preferredLanguage, CancellationToken ct);

    /// <summary>Force a password change + clear the lockout (flags only).</summary>
    Task ResetAccessAsync(Guid actorUserId, Guid targetUserId, Guid tenantId, string reason, CancellationToken ct);
}
