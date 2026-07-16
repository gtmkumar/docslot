using System.Net;
using mediq.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace mediq.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps a CLR <c>string</c> to a PostgreSQL <c>inet</c> column. Npgsql models inet as <see cref="IPAddress"/>;
/// our entities keep it as a friendly string, so we convert at the boundary. Empty/invalid → null.
/// </summary>
internal static class InetConverters
{
    public static readonly ValueConverter<string?, IPAddress?> StringToInet = new(
        s => string.IsNullOrWhiteSpace(s) ? null : IPAddress.Parse(s),
        ip => ip == null ? null : ip.ToString());

    public static readonly ValueConverter<string, IPAddress> StringToInetRequired = new(
        s => IPAddress.Parse(string.IsNullOrWhiteSpace(s) ? "0.0.0.0" : s),
        ip => ip.ToString());
}

/// <summary>
/// Maps <see cref="User"/> to <c>platform.users</c>. Only the identity/auth columns slice 01 uses are
/// mapped; unmapped columns (mfa_secret, sso_subject, compliance fields, ...) are simply ignored by EF
/// — the canonical schema keeps them. <c>citext</c> email maps cleanly to string via Npgsql.
/// </summary>
public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users", "platform");
        b.HasKey(u => u.UserId);
        b.Property(u => u.UserId).HasColumnName("user_id");
        b.Property(u => u.Email).HasColumnName("email").HasColumnType("citext");
        b.Property(u => u.Phone).HasColumnName("phone");
        b.Property(u => u.PasswordHash).HasColumnName("password_hash");
        b.Property(u => u.FullName).HasColumnName("full_name");
        b.Property(u => u.EmailVerified).HasColumnName("email_verified");
        b.Property(u => u.PhoneVerified).HasColumnName("phone_verified");
        b.Property(u => u.MfaEnabled).HasColumnName("mfa_enabled");
        b.Property(u => u.SsoProvider).HasColumnName("sso_provider");
        b.Property(u => u.LastLoginAt).HasColumnName("last_login_at");
        b.Property(u => u.LastLoginIp).HasColumnName("last_login_ip").HasColumnType("inet")
            .HasConversion(InetConverters.StringToInet);
        b.Property(u => u.FailedLoginCount).HasColumnName("failed_login_count");
        b.Property(u => u.LockedUntil).HasColumnName("locked_until");
        b.Property(u => u.MustChangePassword).HasColumnName("must_change_password");
        b.Property(u => u.PreferredLanguage).HasColumnName("preferred_language");
        b.Property(u => u.Timezone).HasColumnName("timezone");
        b.Property(u => u.IsActive).HasColumnName("is_active");
        b.Property(u => u.IsPlatformUser).HasColumnName("is_platform_user");
        b.Property(u => u.CreatedAt).HasColumnName("created_at");
        b.Property(u => u.UpdatedAt).HasColumnName("updated_at");
        b.Property(u => u.DeletedAt).HasColumnName("deleted_at");
    }
}

public sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.ToTable("tenants", "platform");
        b.HasKey(t => t.TenantId);
        b.Property(t => t.TenantId).HasColumnName("tenant_id");
        b.Property(t => t.TenantCode).HasColumnName("tenant_code");
        b.Property(t => t.LegalName).HasColumnName("legal_name");
        b.Property(t => t.DisplayName).HasColumnName("display_name");
        b.Property(t => t.TenantType).HasColumnName("tenant_type");
        b.Property(t => t.PrimaryEmail).HasColumnName("primary_email").HasColumnType("citext");
        b.Property(t => t.PrimaryPhone).HasColumnName("primary_phone");
        b.Property(t => t.Country).HasColumnName("country");
        b.Property(t => t.City).HasColumnName("city");
        b.Property(t => t.State).HasColumnName("state");
        b.Property(t => t.PinCode).HasColumnName("pin_code");
        b.Property(t => t.Timezone).HasColumnName("timezone");
        b.Property(t => t.Status).HasColumnName("status");
        b.Property(t => t.SuspendedReason).HasColumnName("suspended_reason");
        b.Property(t => t.CreatedAt).HasColumnName("created_at");
        b.Property(t => t.UpdatedAt).HasColumnName("updated_at");
        b.Property(t => t.DeletedAt).HasColumnName("deleted_at");
    }
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("roles", "platform");
        b.HasKey(r => r.RoleId);
        b.Property(r => r.RoleId).HasColumnName("role_id");
        b.Property(r => r.RoleKey).HasColumnName("role_key");
        b.Property(r => r.Name).HasColumnName("name");
        b.Property(r => r.Description).HasColumnName("description");
        b.Property(r => r.TenantId).HasColumnName("tenant_id");
        b.Property(r => r.Scope).HasColumnName("scope");
        b.Property(r => r.IsSystem).HasColumnName("is_system");
        b.Property(r => r.IsDefault).HasColumnName("is_default");
        b.Property(r => r.CreatedAt).HasColumnName("created_at");
        b.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        b.Property(r => r.DeletedAt).HasColumnName("deleted_at");
    }
}

public sealed class UserTenantRoleConfiguration : IEntityTypeConfiguration<UserTenantRole>
{
    public void Configure(EntityTypeBuilder<UserTenantRole> b)
    {
        b.ToTable("user_tenant_roles", "platform");
        b.HasKey(x => x.UserTenantRoleId);
        b.Property(x => x.UserTenantRoleId).HasColumnName("user_tenant_role_id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.RoleId).HasColumnName("role_id");
        b.Property(x => x.IsPrimary).HasColumnName("is_primary");
        b.Property(x => x.GrantedAt).HasColumnName("granted_at");
        b.Property(x => x.GrantedBy).HasColumnName("granted_by");
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        b.Property(x => x.RevokedBy).HasColumnName("revoked_by");
        b.Property(x => x.RevokedReason).HasColumnName("revoked_reason");
        b.Property(x => x.BranchId).HasColumnName("branch_id");
        b.Property(x => x.Department).HasColumnName("department");
    }
}

/// <summary>Maps <c>platform.branches</c> — a tenant's physical locations (org display attribute).</summary>
public sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> b)
    {
        b.ToTable("branches", "platform");
        b.HasKey(x => x.BranchId);
        b.Property(x => x.BranchId).HasColumnName("branch_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Code).HasColumnName("code");
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
    }
}

public sealed class UserPermissionOverrideConfiguration : IEntityTypeConfiguration<UserPermissionOverride>
{
    public void Configure(EntityTypeBuilder<UserPermissionOverride> b)
    {
        b.ToTable("user_permission_overrides", "platform");
        b.HasKey(x => x.OverrideId);
        b.Property(x => x.OverrideId).HasColumnName("override_id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.PermissionId).HasColumnName("permission_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.IsAllowed).HasColumnName("is_allowed");
        b.Property(x => x.Reason).HasColumnName("reason");
        b.Property(x => x.GrantedByUserId).HasColumnName("granted_by_user_id");
        b.Property(x => x.EffectiveFrom).HasColumnName("effective_from");
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        b.Property(x => x.IsActiveFlag).HasColumnName("is_active");
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> b)
    {
        b.ToTable("permissions", "platform");
        b.HasKey(p => p.PermissionId);
        b.Property(p => p.PermissionId).HasColumnName("permission_id");
        b.Property(p => p.PermissionKey).HasColumnName("permission_key");
        b.Property(p => p.Resource).HasColumnName("resource");
        b.Property(p => p.Action).HasColumnName("action");
        b.Property(p => p.Scope).HasColumnName("scope");
        b.Property(p => p.Description).HasColumnName("description");
        b.Property(p => p.IsDangerous).HasColumnName("is_dangerous");
    }
}

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("role_permissions", "platform");
        b.HasKey(rp => new { rp.RoleId, rp.PermissionId });
        b.Property(rp => rp.RoleId).HasColumnName("role_id");
        b.Property(rp => rp.PermissionId).HasColumnName("permission_id");
        b.Property(rp => rp.IsGrantable).HasColumnName("is_grantable");
        b.Property(rp => rp.GrantedAt).HasColumnName("granted_at");
    }
}

public sealed class ResourceTypeConfiguration : IEntityTypeConfiguration<ResourceType>
{
    public void Configure(EntityTypeBuilder<ResourceType> b)
    {
        b.ToTable("resource_types", "platform");
        b.HasKey(r => r.ResourceTypeId);
        b.Property(r => r.ResourceTypeId).HasColumnName("resource_type_id");
        b.Property(r => r.ResourceKey).HasColumnName("resource_key");
        b.Property(r => r.ResourceName).HasColumnName("resource_name");
        b.Property(r => r.ProductId).HasColumnName("product_id");
        b.Property(r => r.Description).HasColumnName("description");
        b.Property(r => r.DisplayOrder).HasColumnName("display_order");
        b.Property(r => r.IsActive).HasColumnName("is_active");
    }
}

public sealed class ActionTypeConfiguration : IEntityTypeConfiguration<ActionType>
{
    public void Configure(EntityTypeBuilder<ActionType> b)
    {
        b.ToTable("action_types", "platform");
        b.HasKey(a => a.ActionTypeId);
        b.Property(a => a.ActionTypeId).HasColumnName("action_type_id");
        b.Property(a => a.ActionKey).HasColumnName("action_key");
        b.Property(a => a.ActionName).HasColumnName("action_name");
        b.Property(a => a.Description).HasColumnName("description");
        b.Property(a => a.IsDangerous).HasColumnName("is_dangerous");
        b.Property(a => a.DisplayOrder).HasColumnName("display_order");
        b.Property(a => a.IsActive).HasColumnName("is_active");
    }
}

public sealed class TenantModuleEntitlementConfiguration : IEntityTypeConfiguration<TenantModuleEntitlement>
{
    public void Configure(EntityTypeBuilder<TenantModuleEntitlement> b)
    {
        b.ToTable("tenant_module_entitlements", "platform");
        b.HasKey(e => e.EntitlementId);
        b.Property(e => e.EntitlementId).HasColumnName("entitlement_id");
        b.Property(e => e.TenantId).HasColumnName("tenant_id");
        b.Property(e => e.ResourceTypeId).HasColumnName("resource_type_id");
        b.Property(e => e.IsLicensed).HasColumnName("is_licensed");
        b.Property(e => e.Reason).HasColumnName("reason");
    }
}

public sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> b)
    {
        b.ToTable("user_sessions", "platform");
        b.HasKey(s => s.SessionId);
        b.Property(s => s.SessionId).HasColumnName("session_id");
        b.Property(s => s.UserId).HasColumnName("user_id");
        b.Property(s => s.TokenHash).HasColumnName("token_hash");
        b.Property(s => s.RefreshTokenHash).HasColumnName("refresh_token_hash");
        b.Property(s => s.ActiveTenantId).HasColumnName("active_tenant_id");
        b.Property(s => s.DeviceInfo).HasColumnName("device_info");
        b.Property(s => s.IpAddress).HasColumnName("ip_address").HasColumnType("inet")
            .HasConversion(InetConverters.StringToInet);
        b.Property(s => s.IssuedAt).HasColumnName("issued_at");
        b.Property(s => s.ExpiresAt).HasColumnName("expires_at");
        b.Property(s => s.RefreshExpiresAt).HasColumnName("refresh_expires_at");
        b.Property(s => s.LastActivityAt).HasColumnName("last_activity_at");
        b.Property(s => s.RevokedAt).HasColumnName("revoked_at");
        b.Property(s => s.RevokedReason).HasColumnName("revoked_reason");
    }
}

public sealed class LoginAttemptConfiguration : IEntityTypeConfiguration<LoginAttempt>
{
    public void Configure(EntityTypeBuilder<LoginAttempt> b)
    {
        b.ToTable("login_attempts", "platform");
        b.HasKey(a => a.AttemptId);
        b.Property(a => a.AttemptId).HasColumnName("attempt_id");
        b.Property(a => a.Email).HasColumnName("email").HasColumnType("citext");
        b.Property(a => a.IpAddress).HasColumnName("ip_address").HasColumnType("inet")
            .HasConversion(InetConverters.StringToInetRequired);
        b.Property(a => a.UserAgent).HasColumnName("user_agent");
        b.Property(a => a.Success).HasColumnName("success");
        b.Property(a => a.FailureReason).HasColumnName("failure_reason");
        b.Property(a => a.AttemptedAt).HasColumnName("attempted_at");
    }
}

/// <summary>Keyless projections for SQL-function results — no table, query-only.</summary>
public sealed class KeylessRowConfiguration
    : IEntityTypeConfiguration<PermissionKeyRow>,
      IEntityTypeConfiguration<MenuRow>,
      IEntityTypeConfiguration<BoolRow>,
      IEntityTypeConfiguration<UserMembershipRow>
{
    public void Configure(EntityTypeBuilder<PermissionKeyRow> b) => b.HasNoKey().ToView(null);
    public void Configure(EntityTypeBuilder<MenuRow> b) => b.HasNoKey().ToView(null);
    public void Configure(EntityTypeBuilder<BoolRow> b) => b.HasNoKey().ToView(null);
    public void Configure(EntityTypeBuilder<UserMembershipRow> b) => b.HasNoKey().ToView(null);
}
