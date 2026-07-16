namespace mediq.Domain.Platform;

/// <summary>Multi-tenant root (maps to <c>platform.tenants</c>). Every product row is scoped by <c>tenant_id</c>.</summary>
public sealed class Tenant
{
    public Guid TenantId { get; private set; }
    public string TenantCode { get; private set; } = default!;
    public string LegalName { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public string TenantType { get; private set; } = default!;   // 'individual_doctor','hospital','pathology_lab',...
    public string PrimaryEmail { get; private set; } = default!;  // citext
    public string PrimaryPhone { get; private set; } = default!;
    public string Country { get; private set; } = "IN";
    public string? City { get; private set; }
    public string? State { get; private set; }
    public string? PinCode { get; private set; }
    public string Timezone { get; private set; } = "Asia/Kolkata";
    public string Status { get; private set; } = "pending";
    public string? SuspendedReason { get; private set; }
    /// <summary>Per-tenant config bag (JSONB). Read-only here; the geo centroid lives under <c>settings.geo</c>.
    /// Written only via parameterised SQL (CreateAsync/UpdateAsync), never through the change tracker.</summary>
    public string Settings { get; private set; } = "{}";
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? DeletedAt { get; private set; }

    private Tenant() { }

    public bool IsActive => Status == "active" && DeletedAt is null;
}
