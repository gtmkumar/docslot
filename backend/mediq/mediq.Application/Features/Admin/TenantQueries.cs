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

public sealed record GetTenantQuery(Guid TenantId) : IQuery<TenantDto>;

public sealed class GetTenantQueryHandler(ITenantRepository tenants)
    : IQueryHandler<GetTenantQuery, TenantDto>
{
    public async Task<TenantDto> Handle(GetTenantQuery query, CancellationToken ct)
    {
        var t = await tenants.GetByIdAsync(query.TenantId, ct)
            ?? throw new KeyNotFoundException("Tenant not found.");
        return new TenantDto(t.TenantId, t.TenantCode, t.DisplayName, t.TenantType,
            t.PrimaryEmail, t.Status, t.Country, t.City);
    }
}

public sealed record ListRolesQuery(Guid? TenantId) : IQuery<IReadOnlyList<RoleDto>>;

public sealed class ListRolesQueryHandler(IRoleAssignmentRepository roles)
    : IQueryHandler<ListRolesQuery, IReadOnlyList<RoleDto>>
{
    public async Task<IReadOnlyList<RoleDto>> Handle(ListRolesQuery query, CancellationToken ct)
    {
        var rows = await roles.ListRolesAsync(query.TenantId, ct);
        return rows.Select(r => new RoleDto(r.RoleId, r.RoleKey, r.Name, r.Scope, r.IsSystem, r.TenantId)).ToList();
    }
}
