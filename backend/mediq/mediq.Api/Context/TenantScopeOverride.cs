using mediq.Application.Abstractions;

namespace mediq.Api.Context;

/// <summary>
/// Request-scoped tenant override for ANONYMOUS, server-trusted entry points (the WhatsApp webhook). The
/// webhook has no JWT, so the tenant is resolved server-side from the trusted <c>phone_number_id</c> map and
/// pushed here BEFORE the booking pipeline runs. <see cref="CurrentUserContext.TenantId"/> falls back to
/// this only when there is no JWT <c>tenant_id</c> claim, so authenticated requests are unaffected.
/// <para>
/// SECURITY: this is set ONLY by trusted edge code after a server-side lookup — never from a client header.
/// It scopes RLS (<c>app.tenant_id</c>) and the tenant passed to <c>CreateBookingCommand</c>.
/// </para>
/// </summary>
public sealed class TenantScopeOverride : ITenantScopeOverride
{
    public Guid? TenantId { get; private set; }

    public void Set(Guid tenantId) => TenantId = tenantId;
}

/// <summary>
/// Request-scoped fallback Idempotency-Key for server-originated command dispatches that did not arrive with
/// an HTTP <c>Idempotency-Key</c> header (the WhatsApp inbound handler sets a deterministic key before
/// dispatching the <c>CreateBookingCommand</c>). The HTTP header always takes precedence.
/// </summary>
public sealed class AmbientIdempotencyKey : IAmbientIdempotencyKey
{
    public string? Key { get; private set; }

    public void Set(string key) => Key = key;
}
