using mediq.SharedDataModel.Docslot.Admin;

namespace mediq.Application.Abstractions;

/// <summary>
/// Read-side directory for a tenant's branches. Projects straight off the DbContext (CQRS read trade-off),
/// tenant-scoped by the read chain's RLS + an explicit predicate. Branches are an ORGANIZATIONAL DISPLAY
/// attribute — never an access boundary.
/// </summary>
public interface IBranchDirectory
{
    Task<IReadOnlyList<BranchDto>> ListActiveByTenantAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>
/// Write-side branch creation. Branches confer NO permissions (no escalation surface), so this is a DIRECT
/// own-tenant insert under RLS (no SECURITY DEFINER indirection) — the ambient UnitOfWork commits it and
/// RLS's WITH CHECK bounds it to the acting tenant. See database/11_rbac_hardening.sql.
/// </summary>
public interface IBranchRepository
{
    Task<Guid> CreateAsync(Guid tenantId, string name, string? code, CancellationToken ct);
}

/// <summary>
/// Write-side membership-scope setter. Resolves the target user's scope-bearing (primary, else earliest)
/// active membership in the tenant, then routes the write through <c>platform.set_membership_scope</c>
/// (SECURITY DEFINER) which re-checks the actor's <c>tenant.users.update</c>, validates the branch belongs
/// to the tenant, and writes ONLY branch_id/department — NEVER role_id, so a scope change can never alter
/// effective access. SQLSTATE 42501/P0002 → 403; 23503 (branch not in tenant) → 409.
/// </summary>
public interface IMembershipScopeWriter
{
    /// <summary>The user's scope-bearing active membership id in the tenant (primary DESC, then granted_at ASC),
    /// or null when the user has no active membership there.</summary>
    Task<Guid?> FindScopeMembershipAsync(Guid userId, Guid tenantId, CancellationToken ct);

    /// <summary>Calls <c>platform.set_membership_scope</c>; returns the affected user_tenant_role_id.</summary>
    Task<Guid> SetScopeAsync(
        Guid actorUserId, Guid userTenantRoleId, Guid? branchId, string? department, CancellationToken ct);
}
