namespace mediq.Domain.Platform;

/// <summary>
/// A tenant's physical branch / location (maps to <c>platform.branches</c>). An ORGANIZATIONAL DISPLAY
/// attribute only: it heads the People "All branches" filter and the "N branches" stat, and combines with
/// <c>user_tenant_roles.department</c> to label a member's scope ("Cardiology · Andheri W"). It confers NO
/// permissions and is never consulted by permission resolution — see database/11_rbac_hardening.sql.
/// </summary>
public sealed class Branch
{
    public Guid BranchId { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string? Code { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? DeletedAt { get; private set; }

    private Branch() { }

    public static Branch Create(Guid tenantId, string name, string? code, DateTime nowUtc)
        => new()
        {
            BranchId = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = name,
            Code = code,
            IsActive = true,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        };
}
