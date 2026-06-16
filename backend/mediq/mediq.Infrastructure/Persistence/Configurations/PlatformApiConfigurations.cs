using mediq.Domain.PlatformApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace mediq.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the 8 <c>platform_api.*</c> tables (database/02_platform_api.sql). Database-first: only the columns
/// the slice uses are mapped; the canonical schema is authoritative. Postgres text[] columns map to
/// <c>string[]</c> via Npgsql natively.
/// </summary>
public sealed class ApiClientConfiguration : IEntityTypeConfiguration<ApiClient>
{
    public void Configure(EntityTypeBuilder<ApiClient> b)
    {
        b.ToTable("api_clients", "platform_api");
        b.HasKey(c => c.ClientId);
        b.Property(c => c.ClientId).HasColumnName("client_id");
        b.Property(c => c.ClientCode).HasColumnName("client_code");
        b.Property(c => c.ClientName).HasColumnName("client_name");
        b.Property(c => c.ClientSecretHash).HasColumnName("client_secret_hash");
        b.Property(c => c.ClientType).HasColumnName("client_type");
        b.Property(c => c.OwnerTenantId).HasColumnName("owner_tenant_id");
        b.Property(c => c.OwnerEmail).HasColumnName("owner_email").HasColumnType("citext");
        b.Property(c => c.OwnerOrganization).HasColumnName("owner_organization");
        b.Property(c => c.RateLimitPerMinute).HasColumnName("rate_limit_per_minute");
        b.Property(c => c.RateLimitPerDay).HasColumnName("rate_limit_per_day");
        b.Property(c => c.BurstLimit).HasColumnName("burst_limit");
        b.Property(c => c.WebhookSigningSecret).HasColumnName("webhook_signing_secret");
        b.Property(c => c.IsActive).HasColumnName("is_active");
        b.Property(c => c.IsVerified).HasColumnName("is_verified");
        b.Property(c => c.VerifiedAt).HasColumnName("verified_at");
        b.Property(c => c.VerifiedBy).HasColumnName("verified_by");
        b.Property(c => c.Purpose).HasColumnName("purpose");
        b.Property(c => c.CreatedAt).HasColumnName("created_at");
        b.Property(c => c.UpdatedAt).HasColumnName("updated_at");
        b.Property(c => c.LastUsedAt).HasColumnName("last_used_at");
        b.Property(c => c.DeletedAt).HasColumnName("deleted_at");
    }
}

public sealed class ApiScopeConfiguration : IEntityTypeConfiguration<ApiScope>
{
    public void Configure(EntityTypeBuilder<ApiScope> b)
    {
        b.ToTable("api_scopes", "platform_api");
        b.HasKey(s => s.ScopeId);
        b.Property(s => s.ScopeId).HasColumnName("scope_id");
        b.Property(s => s.ScopeKey).HasColumnName("scope_key");
        b.Property(s => s.Resource).HasColumnName("resource");
        b.Property(s => s.Action).HasColumnName("action");
        b.Property(s => s.Description).HasColumnName("description");
        b.Property(s => s.IsDangerous).HasColumnName("is_dangerous");
        b.Property(s => s.RequiresConsent).HasColumnName("requires_consent");
    }
}

public sealed class ApiClientScopeConfiguration : IEntityTypeConfiguration<ApiClientScope>
{
    public void Configure(EntityTypeBuilder<ApiClientScope> b)
    {
        b.ToTable("api_client_scopes", "platform_api");
        b.HasKey(x => new { x.ClientId, x.ScopeId });
        b.Property(x => x.ClientId).HasColumnName("client_id");
        b.Property(x => x.ScopeId).HasColumnName("scope_id");
        b.Property(x => x.GrantedAt).HasColumnName("granted_at");
        b.Property(x => x.GrantedBy).HasColumnName("granted_by");
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
    }
}

public sealed class ApiTokenConfiguration : IEntityTypeConfiguration<ApiToken>
{
    public void Configure(EntityTypeBuilder<ApiToken> b)
    {
        b.ToTable("api_tokens", "platform_api");
        b.HasKey(t => t.TokenId);
        b.Property(t => t.TokenId).HasColumnName("token_id");
        b.Property(t => t.ClientId).HasColumnName("client_id");
        b.Property(t => t.TokenHash).HasColumnName("token_hash");
        b.Property(t => t.RequestedScopes).HasColumnName("requested_scopes");
        b.Property(t => t.GrantedScopes).HasColumnName("granted_scopes");
        b.Property(t => t.TenantId).HasColumnName("tenant_id");
        b.Property(t => t.UserId).HasColumnName("user_id");
        b.Property(t => t.IssuedAt).HasColumnName("issued_at");
        b.Property(t => t.ExpiresAt).HasColumnName("expires_at");
        b.Property(t => t.RevokedAt).HasColumnName("revoked_at");
    }
}

public sealed class WebhookSubscriptionConfiguration : IEntityTypeConfiguration<WebhookSubscription>
{
    public void Configure(EntityTypeBuilder<WebhookSubscription> b)
    {
        b.ToTable("webhook_subscriptions", "platform_api");
        b.HasKey(w => w.WebhookId);
        b.Property(w => w.WebhookId).HasColumnName("webhook_id");
        b.Property(w => w.ClientId).HasColumnName("client_id");
        b.Property(w => w.TenantId).HasColumnName("tenant_id");
        b.Property(w => w.Name).HasColumnName("name");
        b.Property(w => w.Url).HasColumnName("url");
        b.Property(w => w.SecretHash).HasColumnName("secret_hash");
        b.Property(w => w.EventTypes).HasColumnName("event_types");
        b.Property(w => w.MaxRetries).HasColumnName("max_retries");
        b.Property(w => w.RetryBackoff).HasColumnName("retry_backoff");
        b.Property(w => w.TimeoutSeconds).HasColumnName("timeout_seconds");
        b.Property(w => w.IsActive).HasColumnName("is_active");
        b.Property(w => w.LastSuccessAt).HasColumnName("last_success_at");
        b.Property(w => w.LastFailureAt).HasColumnName("last_failure_at");
        b.Property(w => w.ConsecutiveFailures).HasColumnName("consecutive_failures");
        b.Property(w => w.AutoDisabledAt).HasColumnName("auto_disabled_at");
        b.Property(w => w.CreatedAt).HasColumnName("created_at");
        b.Property(w => w.UpdatedAt).HasColumnName("updated_at");
    }
}

public sealed class ApiEventTypeConfiguration : IEntityTypeConfiguration<ApiEventType>
{
    public void Configure(EntityTypeBuilder<ApiEventType> b)
    {
        b.ToTable("api_event_types", "platform_api");
        b.HasKey(e => e.EventType);
        b.Property(e => e.EventType).HasColumnName("event_type");
        b.Property(e => e.Resource).HasColumnName("resource");
        b.Property(e => e.Action).HasColumnName("action");
        b.Property(e => e.Description).HasColumnName("description");
        b.Property(e => e.RequiresScope).HasColumnName("requires_scope");
        b.Property(e => e.IsActive).HasColumnName("is_active");
    }
}

public sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> b)
    {
        b.ToTable("webhook_deliveries", "platform_api");
        b.HasKey(d => d.DeliveryId);
        b.Property(d => d.DeliveryId).HasColumnName("delivery_id");
        b.Property(d => d.WebhookId).HasColumnName("webhook_id");
        b.Property(d => d.EventType).HasColumnName("event_type");
        b.Property(d => d.EventId).HasColumnName("event_id");
        b.Property(d => d.Payload).HasColumnName("payload").HasColumnType("jsonb");
        b.Property(d => d.Signature).HasColumnName("signature");
        b.Property(d => d.Status).HasColumnName("status");
        b.Property(d => d.AttemptCount).HasColumnName("attempt_count");
        b.Property(d => d.ResponseStatusCode).HasColumnName("response_status_code");
        b.Property(d => d.ResponseTimeMs).HasColumnName("response_time_ms");
        b.Property(d => d.ErrorMessage).HasColumnName("error_message");
        b.Property(d => d.NextRetryAt).HasColumnName("next_retry_at");
        b.Property(d => d.CreatedAt).HasColumnName("created_at");
        b.Property(d => d.DeliveredAt).HasColumnName("delivered_at");
    }
}
