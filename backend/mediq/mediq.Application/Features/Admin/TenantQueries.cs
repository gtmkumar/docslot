using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Admin;

namespace mediq.Application.Features.Admin;

public sealed record ListTenantsQuery(int Skip = 0, int Take = 50) : IQuery<IReadOnlyList<TenantDto>>;

public sealed class ListTenantsQueryHandler(ITenantRepository tenants)
    : IQueryHandler<ListTenantsQuery, IReadOnlyList<TenantDto>>
{
    public async Task<IReadOnlyList<TenantDto>> Handle(ListTenantsQuery query, CancellationToken ct)
    {
        var rows = await tenants.ListAsync(query.Skip, Math.Clamp(query.Take, 1, 200), ct);
        return rows.Select(t => new TenantDto(
            t.TenantId, t.TenantCode, t.DisplayName, t.TenantType,
            t.PrimaryEmail, t.Status, t.Country, t.City)).ToList();
    }
}

/// <summary>Detail read for the edit surface — returns the full <see cref="TenantDetailDto"/> (superset of the
/// list DTO) so the form pre-fills every editable field. Reuses <see cref="ITenantRepository.GetByIdAsync"/>,
/// which already loads the full entity; this only widens the projection.</summary>
public sealed record GetTenantQuery(Guid TenantId) : IQuery<TenantDetailDto>;

public sealed class GetTenantQueryHandler(ITenantRepository tenants)
    : IQueryHandler<GetTenantQuery, TenantDetailDto>
{
    public async Task<TenantDetailDto> Handle(GetTenantQuery query, CancellationToken ct)
    {
        var t = await tenants.GetByIdAsync(query.TenantId, ct)
            ?? throw new KeyNotFoundException("Tenant not found.");
        var (latitude, longitude) = TenantGeo.Read(t.Settings);
        return new TenantDetailDto(t.TenantId, t.TenantCode, t.DisplayName, t.TenantType,
            t.LegalName, t.PrimaryEmail, t.PrimaryPhone, t.Status, t.Country, t.City, t.State, t.PinCode,
            t.SuspendedReason, latitude, longitude);
    }
}

public sealed record ListRolesQuery(Guid? TenantId) : IQuery<IReadOnlyList<RoleDto>>;

public sealed class ListRolesQueryHandler(IRoleAssignmentRepository roles, ICurrentUserContext ctx)
    : IQueryHandler<ListRolesQuery, IReadOnlyList<RoleDto>>
{
    public Task<IReadOnlyList<RoleDto>> Handle(ListRolesQuery query, CancellationToken ct)
    {
        // Default to the caller's tenant so a tenant admin sees system roles PLUS their own custom roles.
        // Without this, an omitted tenantId returns only global/system rows and custom roles vanish from
        // the list (they carry a tenant_id). A super_admin can still target another tenant explicitly.
        // MemberCount is computed for that same resolved tenant scope in a single grouped query.
        var tenantId = query.TenantId ?? ctx.TenantId;
        return roles.ListRolesWithMemberCountsAsync(tenantId, ct);
    }
}
