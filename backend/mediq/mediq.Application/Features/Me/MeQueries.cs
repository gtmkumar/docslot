using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.SharedDataModel.Docslot.Navigation;

namespace mediq.Application.Features.Me;

// ---- GET /api/v1/me -------------------------------------------------------------------------------

public sealed record GetMeQuery(Guid UserId, Guid? ActiveTenantId) : IQuery<MeDto>;

public sealed class GetMeQueryHandler(IUserRepository users, ITenantRepository tenants)
    : IQueryHandler<GetMeQuery, MeDto>
{
    public async Task<MeDto> Handle(GetMeQuery query, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(query.UserId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        var memberships = await tenants.GetMembershipsAsync(query.UserId, ct);
        var tenantDtos = memberships
            .Select(m => new MeTenantDto(m.TenantId, m.TenantCode, m.DisplayName, m.TenantType, m.IsPrimary))
            .ToList();

        // Role display labels for the ACTIVE tenant (sidebar chip). Display-only — never an authz input.
        var roleDtos = query.ActiveTenantId is { } activeTenantId
            ? (await tenants.GetRoleLabelsAsync(query.UserId, activeTenantId, ct))
                .Select(r => new MeRoleDto(r.RoleKey, r.Name)).ToList()
            : [];

        return new MeDto(
            user.UserId, user.Email, user.FullName, user.PreferredLanguage, user.Timezone,
            user.MfaEnabled, query.ActiveTenantId, tenantDtos, roleDtos);
    }
}

// ---- GET /api/v1/me/permissions -------------------------------------------------------------------

/// <summary>
/// Returns the resolve-once-per-request effective set. The authorization middleware already resolved it
/// into <see cref="IPermissionContext"/>; this handler reads that cache (no extra DB call), falling back
/// to the RBAC service only if the cache was never populated (e.g. direct call in a test).
/// </summary>
public sealed record GetMyPermissionsQuery(Guid UserId, Guid? TenantId) : IQuery<PermissionSetDto>;

public sealed class GetMyPermissionsQueryHandler(IPermissionContext permissions, IRbacQueryService rbac)
    : IQueryHandler<GetMyPermissionsQuery, PermissionSetDto>
{
    public async Task<PermissionSetDto> Handle(GetMyPermissionsQuery query, CancellationToken ct)
    {
        var keys = permissions.IsResolved
            ? permissions.Keys
            : await rbac.ResolvePermissionsAsync(query.UserId, query.TenantId, ct);

        return new PermissionSetDto(query.UserId, query.TenantId ?? Guid.Empty, keys);
    }
}

// ---- GET /api/v1/me/menus -------------------------------------------------------------------------

/// <summary>A null <paramref name="TenantId"/> is the PLATFORM scope: a platform user (super_admin) with
/// no active tenant gets the global menu set filtered by their platform-level permissions — the sidebar
/// stays backend-driven for every session shape (never a 403 that blanks the nav).</summary>
public sealed record GetMyMenusQuery(Guid UserId, Guid? TenantId, string? TenantType, string ProductKey = "docslot")
    : IQuery<IReadOnlyList<MenuNodeDto>>;

public sealed class GetMyMenusQueryHandler(IRbacQueryService rbac)
    : IQueryHandler<GetMyMenusQuery, IReadOnlyList<MenuNodeDto>>
{
    public Task<IReadOnlyList<MenuNodeDto>> Handle(GetMyMenusQuery query, CancellationToken ct)
        => rbac.GetMenusAsync(query.UserId, query.TenantId, query.TenantType, query.ProductKey, ct);
}

// ---- GET /api/v1/me/badges ------------------------------------------------------------------------

/// <summary>
/// Badge counts keyed by <c>platform.navigation_menus.badge_source</c>. The key set is discovered from the
/// schema and the computable counts (pending/today bookings) are filled per active tenant; uncomputable
/// keys are returned as 0. With no active tenant the map is empty (badges are tenant-scoped).
/// </summary>
public sealed record GetMyBadgesQuery(Guid UserId, Guid? TenantId) : IQuery<BadgesDto>;

public sealed class GetMyBadgesQueryHandler(IBadgeReadService badges)
    : IQueryHandler<GetMyBadgesQuery, BadgesDto>
{
    public async Task<BadgesDto> Handle(GetMyBadgesQuery query, CancellationToken ct)
    {
        if (query.TenantId is not { } tenantId)
            return new BadgesDto(new Dictionary<string, int>());

        var counts = await badges.GetBadgeCountsAsync(tenantId, ct);
        return new BadgesDto(counts);
    }
}
