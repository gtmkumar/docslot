using System.Reflection;
using mediq.Domain.Commission;
using mediq.Domain.Docslot;
using mediq.Domain.Platform;
using mediq.Domain.PlatformApi;
using Microsoft.EntityFrameworkCore;

namespace mediq.Infrastructure.Persistence;

/// <summary>
/// Database-first context mapped to the canonical <c>platform</c> schema (database/01_platform_core.sql +
/// 08_rbac_navigation.sql). Slice 01 maps only the identity/auth/RBAC tables it needs; the schema is
/// authoritative — EF migrations would only ever TRACK drift, never own these tables.
/// <para>
/// RBAC resolution (<c>resolve_user_permissions</c>, <c>get_user_menus</c>, <c>user_has_permission</c>)
/// is invoked via <c>FromSqlRaw</c> against keyless projection types, never reimplemented in C#.
/// </para>
/// </summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserTenantRole> UserTenantRoles => Set<UserTenantRole>();
    public DbSet<UserPermissionOverride> UserPermissionOverrides => Set<UserPermissionOverride>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<ResourceType> ResourceTypes => Set<ResourceType>();
    public DbSet<ActionType> ActionTypes => Set<ActionType>();
    public DbSet<TenantModuleEntitlement> TenantModuleEntitlements => Set<TenantModuleEntitlement>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();

    // platform_api (slice 02).
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();
    public DbSet<ApiScope> ApiScopes => Set<ApiScope>();
    public DbSet<ApiClientScope> ApiClientScopes => Set<ApiClientScope>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<ApiEventType> ApiEventTypes => Set<ApiEventType>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    // docslot (slice 03 — booking core; clinical PHI tables deferred to 03b/05).
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<Doctor> Doctors => Set<Doctor>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<PatientTenantLink> PatientTenantLinks => Set<PatientTenantLink>();
    public DbSet<TimeSlot> TimeSlots => Set<TimeSlot>();

    // docslot clinical PHI (slice 03b — encrypted at rest, RLS-protected).
    public DbSet<Prescription> Prescriptions => Set<Prescription>();
    public DbSet<LabReport> LabReports => Set<LabReport>();
    public DbSet<MedicalHistory> MedicalHistories => Set<MedicalHistory>();
    public DbSet<AbdmHealthRecord> AbdmHealthRecords => Set<AbdmHealthRecord>();

    // commission (slice 07 — broker referral economy).
    public DbSet<Broker> Brokers => Set<Broker>();
    public DbSet<Attribution> Attributions => Set<Attribution>();
    public DbSet<CommissionRule> CommissionRules => Set<CommissionRule>();
    public DbSet<Payout> Payouts => Set<Payout>();

    // Keyless projections for SQL-function results.
    public DbSet<PermissionKeyRow> PermissionKeyRows => Set<PermissionKeyRow>();
    public DbSet<MenuRow> MenuRows => Set<MenuRow>();
    public DbSet<BoolRow> BoolRows => Set<BoolRow>();
    public DbSet<UserMembershipRow> UserMembershipRows => Set<UserMembershipRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("platform");
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>Single-column result of <c>resolve_user_permissions()</c>.</summary>
public sealed class PermissionKeyRow
{
    public string PermissionKey { get; set; } = default!;
}

/// <summary>Boolean scalar result wrapper for <c>user_has_permission()</c>.</summary>
public sealed class BoolRow
{
    public bool Value { get; set; }
}

/// <summary>Flat row from <c>get_user_menus()</c> before tree assembly.</summary>
public sealed class MenuRow
{
    public Guid MenuId { get; set; }
    public Guid? ParentMenuId { get; set; }
    public string MenuKey { get; set; } = default!;
    public string MenuLabel { get; set; } = default!;
    public string? MenuLabelHi { get; set; }
    public string? MenuIcon { get; set; }
    public string? MenuUrl { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsSectionHeader { get; set; }
    public string? BadgeSource { get; set; }
}

/// <summary>Projection of the tenants a user belongs to (join user_tenant_roles → tenants).</summary>
public sealed class UserMembershipRow
{
    public Guid TenantId { get; set; }
    public string TenantCode { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string TenantType { get; set; } = default!;
    public bool IsPrimary { get; set; }
}
