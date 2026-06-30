using System.Security.Cryptography;
using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.PlatformApi;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.PlatformApi.Clients;

// ---- List / get clients + scopes registry (reads) ------------------------------------------------

public sealed record ListApiClientsQuery(int Skip = 0, int Take = 50) : IQuery<IReadOnlyList<ApiClientDto>>;

public sealed class ListApiClientsQueryHandler(IApiClientRepository clients)
    : IQueryHandler<ListApiClientsQuery, IReadOnlyList<ApiClientDto>>
{
    public Task<IReadOnlyList<ApiClientDto>> Handle(ListApiClientsQuery q, CancellationToken ct)
        => clients.ListAsync(q.Skip, Math.Clamp(q.Take, 1, 200), ct);
}

public sealed record ListScopesQuery : IQuery<IReadOnlyList<ScopeDto>>;

public sealed class ListScopesQueryHandler(IApiScopeRepository scopes)
    : IQueryHandler<ListScopesQuery, IReadOnlyList<ScopeDto>>
{
    public Task<IReadOnlyList<ScopeDto>> Handle(ListScopesQuery q, CancellationToken ct) => scopes.ListAsync(ct);
}

// ---- Register client (manual-approval: created inactive + unverified) -----------------------------

// IDoNotCacheResponse: the result carries the plaintext client secret (returned once). It must never be
// persisted to the plaintext idempotency store now that the portal POSTs this live with an Idempotency-Key.
public sealed record RegisterApiClientCommand(RegisterApiClientRequest Request) : ICommand<ApiClientSecretResult>, IDoNotCacheResponse;

public sealed class RegisterApiClientValidator : AbstractValidator<RegisterApiClientCommand>
{
    public RegisterApiClientValidator()
    {
        RuleFor(x => x.Request.ClientCode).NotEmpty().MaximumLength(50).Matches("^[a-z0-9-]+$");
        RuleFor(x => x.Request.ClientName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Request.ClientType).Must(t => t is "first_party" or "partner" or "public");
        RuleFor(x => x.Request.OwnerEmail).NotEmpty().EmailAddress();
        RuleFor(x => x.Request.Purpose).NotEmpty().WithMessage("A purpose is required (compliance).");
    }
}

public sealed class RegisterApiClientCommandHandler(
    IApiClientRepository clients, IPasswordHasher hasher, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<RegisterApiClientCommand, ApiClientSecretResult>
{
    public async Task<ApiClientSecretResult> Handle(RegisterApiClientCommand command, CancellationToken ct)
    {
        var req = command.Request;
        if (await clients.GetByCodeAsync(req.ClientCode, ct) is not null)
            throw new BusinessRuleException($"A client with code '{req.ClientCode}' already exists.");

        // Generate a strong secret; store ONLY its hash; return plaintext once.
        var secret = ClientSecretGenerator.New();
        var secretHash = hasher.Hash(secret);
        var clientId = await clients.CreateAsync(req, secretHash, clock.UtcNow, ct);

        await audit.RecordAsync(new AuditEntry(
            "register", "api_client", clientId, req.ClientCode, ctx.UserId, req.OwnerTenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Registered API client {req.ClientCode} ({req.ClientType}) — pending approval"), ct);

        return new ApiClientSecretResult(clientId, req.ClientCode, secret);
    }
}

// ---- Rotate secret -------------------------------------------------------------------------------

// IDoNotCacheResponse: returns the new plaintext secret once — never persist it to the idempotency store.
public sealed record RotateClientSecretCommand(Guid ClientId) : ICommand<ApiClientSecretResult>, IDoNotCacheResponse;

public sealed class RotateClientSecretCommandHandler(
    IApiClientRepository clients, IPasswordHasher hasher, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<RotateClientSecretCommand, ApiClientSecretResult>
{
    public async Task<ApiClientSecretResult> Handle(RotateClientSecretCommand command, CancellationToken ct)
    {
        var client = await clients.GetByIdAsync(command.ClientId, ct)
            ?? throw new KeyNotFoundException("API client not found.");

        var secret = ClientSecretGenerator.New();
        await clients.UpdateSecretHashAsync(client.ClientId, hasher.Hash(secret), ct);

        await audit.RecordAsync(new AuditEntry(
            "rotate_secret", "api_client", client.ClientId, client.ClientCode, ctx.UserId, client.OwnerTenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: "Client secret rotated"), ct);

        return new ApiClientSecretResult(client.ClientId, client.ClientCode, secret);
    }
}

// ---- Approve / suspend ---------------------------------------------------------------------------

public sealed record SetClientStatusCommand(Guid ClientId, SetClientStatusRequest Request) : ICommand<Unit>;

public sealed class SetClientStatusCommandHandler(
    IApiClientRepository clients, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<SetClientStatusCommand>
{
    public async Task<Unit> Handle(SetClientStatusCommand command, CancellationToken ct)
    {
        var client = await clients.GetByIdAsync(command.ClientId, ct)
            ?? throw new KeyNotFoundException("API client not found.");

        await clients.SetStatusAsync(client.ClientId, command.Request.IsActive, command.Request.IsVerified,
            command.Request.IsVerified ? ctx.UserId : null, clock.UtcNow, ct);

        await audit.RecordAsync(new AuditEntry(
            "set_status", "api_client", client.ClientId, client.ClientCode, ctx.UserId, client.OwnerTenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"active={command.Request.IsActive}, verified={command.Request.IsVerified}. {command.Request.Reason}"), ct);
        return Unit.Value;
    }
}

// ---- Set scopes ----------------------------------------------------------------------------------

public sealed record SetClientScopesCommand(Guid ClientId, SetClientScopesRequest Request) : ICommand<Unit>;

public sealed class SetClientScopesCommandHandler(
    IApiClientRepository clients, IApiScopeRepository scopes, IAuditTrailWriter audit, ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<SetClientScopesCommand>
{
    public async Task<Unit> Handle(SetClientScopesCommand command, CancellationToken ct)
    {
        var client = await clients.GetByIdAsync(command.ClientId, ct)
            ?? throw new KeyNotFoundException("API client not found.");

        // Every requested scope key must exist in the registry (fail-closed on typos / unknown scopes).
        var existing = await scopes.ExistingScopeKeysAsync(command.Request.ScopeKeys, ct);
        var unknown = command.Request.ScopeKeys.Where(s => !existing.Contains(s)).ToArray();
        if (unknown.Length != 0)
            throw new BusinessRuleException($"Unknown scope(s): {string.Join(", ", unknown)}");

        await clients.SetScopesAsync(client.ClientId, command.Request.ScopeKeys, ctx.UserId, clock.UtcNow, ct);

        await audit.RecordAsync(new AuditEntry(
            "set_scopes", "api_client", client.ClientId, client.ClientCode, ctx.UserId, client.OwnerTenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Scopes set: {string.Join(' ', command.Request.ScopeKeys)}"), ct);
        return Unit.Value;
    }
}

// ---- Set rate limits -----------------------------------------------------------------------------

public sealed record SetClientRateLimitsCommand(Guid ClientId, SetClientRateLimitsRequest Request) : ICommand<Unit>;

public sealed class SetClientRateLimitsValidator : AbstractValidator<SetClientRateLimitsCommand>
{
    public SetClientRateLimitsValidator()
    {
        RuleFor(x => x.Request.RateLimitPerMinute).GreaterThan(0);
        RuleFor(x => x.Request.RateLimitPerDay).GreaterThan(0);
        RuleFor(x => x.Request.BurstLimit).GreaterThan(0);
    }
}

public sealed class SetClientRateLimitsCommandHandler(
    IApiClientRepository clients, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<SetClientRateLimitsCommand>
{
    public async Task<Unit> Handle(SetClientRateLimitsCommand command, CancellationToken ct)
    {
        var client = await clients.GetByIdAsync(command.ClientId, ct)
            ?? throw new KeyNotFoundException("API client not found.");
        var r = command.Request;
        await clients.SetRateLimitsAsync(client.ClientId, r.RateLimitPerMinute, r.RateLimitPerDay, r.BurstLimit, ct);
        await audit.RecordAsync(new AuditEntry(
            "set_rate_limits", "api_client", client.ClientId, client.ClientCode, ctx.UserId, client.OwnerTenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"per_minute={r.RateLimitPerMinute}, per_day={r.RateLimitPerDay}, burst={r.BurstLimit}"), ct);
        return Unit.Value;
    }
}

/// <summary>Cryptographically-strong client/webhook secret generator (URL-safe base64).</summary>
internal static class ClientSecretGenerator
{
    public static string New() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
