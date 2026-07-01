using mediq.Application.Abstractions;
using mediq.Infrastructure.Docslot;
using mediq.SharedDataModel.Docslot.Admin;
using Microsoft.EntityFrameworkCore;

namespace mediq.Infrastructure.Persistence;

/// <summary>
/// Read-side user directory. Lists users in a tenant, projecting straight to the DTO (CQRS read trade-off).
/// <para>
/// Includes both ACTIVE members and members who were DEACTIVATED in this tenant (their memberships are
/// soft-revoked with a "deactivated: " marker) so the manage panel can show — and reactivate — them. Users
/// who genuinely left (memberships revoked without the marker) drop off the list. <c>IsActive</c> is the
/// TENANT activity flag (≥1 active membership AND globally active), not the global users.is_active column.
/// Phone is MASKED server-side (DPDP: raw phone never crosses the wire in an aggregate).
/// </para>
/// </summary>
public sealed class UserDirectory(PlatformDbContext db) : IUserDirectory
{
    /// <summary>Reserved marker prefix written into revoked_reason by platform.set_tenant_user_active on
    /// deactivate (revoke_role_assignment rejects this prefix, so only that routine can produce it).</summary>
    private const string DeactivationMarker = "[deactivated] ";

    public async Task<IReadOnlyList<UserListItemDto>> ListByTenantAsync(Guid tenantId, int skip, int take, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // 1) The page of users who are ACTIVE members of the tenant OR were deactivated here (marked-revoked).
        var users = await (
            from u in db.Users.AsNoTracking()
            where u.DeletedAt == null
                  && db.UserTenantRoles.Any(utr =>
                        utr.UserId == u.UserId
                        && utr.TenantId == tenantId
                        && (
                              (utr.RevokedAt == null && (utr.ExpiresAt == null || utr.ExpiresAt > now))      // active member
                              || (utr.RevokedAt != null && utr.RevokedReason != null
                                    && utr.RevokedReason.StartsWith(DeactivationMarker))                     // deactivated here
                           ))
            orderby u.FullName
            select new
            {
                u.UserId, u.Email, u.FullName, u.Phone, u.IsActive, u.MfaEnabled,
                u.LastLoginAt, u.LockedUntil, u.MustChangePassword,
            })
            .Skip(skip).Take(take)
            .ToListAsync(ct);

        var userIds = users.Select(u => u.UserId).ToList();

        // 2) Their ACTIVE role assignments in this tenant (deactivated users have none → empty chips, Inactive).
        var roleRows = await (
            from utr in db.UserTenantRoles.AsNoTracking()
            join r in db.Roles.AsNoTracking() on utr.RoleId equals r.RoleId
            where utr.TenantId == tenantId
                  && utr.RevokedAt == null
                  && (utr.ExpiresAt == null || utr.ExpiresAt > now)
                  && r.DeletedAt == null
                  && userIds.Contains(utr.UserId)
            select new
            {
                utr.UserId, utr.UserTenantRoleId, r.RoleId, r.RoleKey, r.Name, utr.IsPrimary, utr.ExpiresAt,
            })
            .ToListAsync(ct);

        var rolesByUser = roleRows
            .GroupBy(x => x.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<UserRoleDto>)g
                    .OrderByDescending(x => x.IsPrimary).ThenBy(x => x.Name)
                    .Select(x => new UserRoleDto(x.UserTenantRoleId, x.RoleId, x.RoleKey, x.Name, x.IsPrimary, x.ExpiresAt))
                    .ToList());

        // 3) Most-recent ACTIVE-session activity per user (issue #87) → the People tab "Online" dot. A session is
        //    live when not revoked and not expired; sessions are cross-tenant identity, so no tenant predicate here.
        var lastActivity = await (
            from s in db.UserSessions.AsNoTracking()
            where userIds.Contains(s.UserId) && s.RevokedAt == null && s.ExpiresAt > now
            group s by s.UserId into g
            select new { UserId = g.Key, LastActivityAt = g.Max(x => (DateTime?)x.LastActivityAt) })
            .ToDictionaryAsync(x => x.UserId, x => x.LastActivityAt, ct);

        return users
            .Select(x =>
            {
                var hasActiveRole = rolesByUser.TryGetValue(x.UserId, out var rs);
                return new UserListItemDto(
                    x.UserId, x.Email, x.FullName,
                    PhoneMasker.Mask(x.Phone),
                    x.IsActive && hasActiveRole,        // active IN THIS TENANT
                    x.MfaEnabled, x.LastLoginAt, x.LockedUntil, x.MustChangePassword,
                    hasActiveRole ? rs! : [],
                    lastActivity.GetValueOrDefault(x.UserId));
            })
            .ToList();
    }
}
