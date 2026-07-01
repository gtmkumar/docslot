using mediq.SharedDataModel.Docslot.Security;

namespace mediq.Application.Abstractions;

/// <summary>
/// Admin oversight of <c>platform.user_sessions</c> (issue #87): list ACTIVE sessions of the caller-tenant's
/// members, and revoke them (per-session or sign-out-all-for-a-user). STRICTLY tenant-scoped: every method
/// joins <c>user_tenant_roles</c> on the caller's tenant, so a session belonging to a user who is NOT a
/// member of that tenant is never listed and never revocable. Token/refresh hashes are never surfaced.
/// </summary>
public interface ISessionAdminService
{
    /// <summary>Active (not revoked, not expired) sessions of users who are members of <paramref name="tenantId"/>.
    /// <paramref name="currentUserId"/> is used only to set the per-row <c>IsSelf</c> flag.</summary>
    Task<IReadOnlyList<ActiveSessionDto>> ListActiveForTenantAsync(
        Guid tenantId, Guid? currentUserId, int take, CancellationToken ct);

    /// <summary>
    /// Revoke a single session, but ONLY if its owner is an active member of <paramref name="tenantId"/>.
    /// Returns true when a row was revoked; false when the session does not exist, is already revoked, or the
    /// owner is not a member of this tenant (so a non-member's session is refused).
    /// </summary>
    Task<bool> RevokeMemberSessionAsync(Guid sessionId, Guid tenantId, string reason, CancellationToken ct);

    /// <summary>
    /// Sign out ALL active sessions for <paramref name="targetUserId"/>, but ONLY if that user is an active
    /// member of <paramref name="tenantId"/>. Throws when the target is not a member (cross-tenant refusal).
    /// Returns the number of sessions revoked.
    /// </summary>
    Task<int> RevokeAllForMemberAsync(Guid targetUserId, Guid tenantId, string reason, CancellationToken ct);
}
