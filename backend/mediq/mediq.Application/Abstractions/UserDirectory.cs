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

/// <summary>Write-side user provisioning (create user + optional initial role assignment).</summary>
public interface IUserProvisioning
{
    Task<Guid> CreateAsync(Guid tenantId, CreateUserRequest request, DateTime nowUtc, CancellationToken ct);
}
