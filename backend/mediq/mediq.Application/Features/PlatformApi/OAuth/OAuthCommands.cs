using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.PlatformApi;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.PlatformApi.OAuth;

/// <summary>
/// OAuth 2.0 client-credentials token issuance. Authenticates client_id+secret (secret verified against
/// the bcrypt hash — never plaintext), confirms the client may issue tokens (active + verified), validates
/// requested scopes ⊆ the client's granted scopes, mints a scoped JWT, and stores its hash for revocation.
/// </summary>
public sealed record IssueClientTokenCommand(OAuthTokenRequest Request) : ICommand<OAuthTokenResponse>;

public sealed class IssueClientTokenValidator : AbstractValidator<IssueClientTokenCommand>
{
    public IssueClientTokenValidator()
    {
        RuleFor(x => x.Request.GrantType).Equal("client_credentials")
            .WithMessage("Only grant_type=client_credentials is supported.");
        RuleFor(x => x.Request.ClientId).NotEmpty();
        RuleFor(x => x.Request.ClientSecret).NotEmpty();
    }
}

public sealed class IssueClientTokenCommandHandler(
    IApiClientRepository clients,
    IPasswordHasher secretHasher,
    ITokenService tokenService,
    IApiTokenStore tokenStore,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx,
    IClock clock)
    : ICommandHandler<IssueClientTokenCommand, OAuthTokenResponse>
{
    public async Task<OAuthTokenResponse> Handle(IssueClientTokenCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var now = clock.UtcNow;

        // Uniform "invalid_client" failure for unknown client, bad secret, or ineligible client — never leak which.
        var client = await clients.GetByCodeAsync(req.ClientId, ct);
        if (client is null
            || !secretHasher.Verify(req.ClientSecret, client.ClientSecretHash)
            || !client.CanIssueToken)
        {
            await audit.RecordAsync(new AuditEntry(
                "oauth_token_denied", "api_client", client?.ClientId, req.ClientId, ctx.UserId, req.TenantId,
                ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: false,
                ChangeSummary: "Client-credentials authentication failed"), ct);
            throw new ForbiddenException("invalid_client");
        }

        // Resolve granted scopes; requested ⊆ granted (else 'invalid_scope').
        var granted = await clients.GetGrantedScopeKeysAsync(client.ClientId, ct);
        var requested = ParseScopes(req.Scope);
        var effective = requested.Count == 0
            ? granted                                   // empty request = all granted scopes
            : requested.Where(granted.Contains).ToHashSet(StringComparer.Ordinal);

        if (requested.Count > 0 && requested.Any(s => !granted.Contains(s)))
            throw new BusinessRuleException("invalid_scope: one or more requested scopes are not granted to this client.");

        if (effective.Count == 0)
            throw new BusinessRuleException("invalid_scope: the client has no usable scopes.");

        // Mint the scoped JWT (reuses the platform signing key — DRY) and persist its hash for revocation.
        var token = tokenService.CreateClientAccessToken(client.ClientId, req.TenantId, effective);
        var tokenHash = tokenService.HashToken(token.Value);
        await tokenStore.CreateAsync(
            client.ClientId, tokenHash, requested.Count == 0 ? effective : requested, effective,
            req.TenantId, token.ExpiresAtUtc, ct);

        await clients.TouchLastUsedAsync(client.ClientId, now, ct);
        await audit.RecordAsync(new AuditEntry(
            "oauth_token_issued", "api_client", client.ClientId, client.ClientCode, ctx.UserId, req.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: $"Issued token with scopes: {string.Join(' ', effective)}"), ct);

        return new OAuthTokenResponse(token.Value, "Bearer", token.ExpiresInSeconds, string.Join(' ', effective));
    }

    private static HashSet<string> ParseScopes(string? scope) =>
        string.IsNullOrWhiteSpace(scope)
            ? new HashSet<string>(StringComparer.Ordinal)
            : scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .ToHashSet(StringComparer.Ordinal);
}

/// <summary>RFC 7009 token revocation — revokes the issued token by its hash (idempotent).</summary>
public sealed record RevokeClientTokenCommand(OAuthRevokeRequest Request) : ICommand<Unit>;

public sealed class RevokeClientTokenValidator : AbstractValidator<RevokeClientTokenCommand>
{
    public RevokeClientTokenValidator() => RuleFor(x => x.Request.Token).NotEmpty();
}

public sealed class RevokeClientTokenCommandHandler(
    ITokenService tokenService, IApiTokenStore tokenStore, IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<RevokeClientTokenCommand>
{
    public async Task<Unit> Handle(RevokeClientTokenCommand command, CancellationToken ct)
    {
        var hash = tokenService.HashToken(command.Request.Token);
        await tokenStore.RevokeByHashAsync(hash, "revoked", ct);   // idempotent — no-op if unknown/already revoked
        await audit.RecordAsync(new AuditEntry(
            "oauth_token_revoked", "api_token", null, null, ctx.UserId, ctx.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true, ChangeSummary: "Client token revoked"), ct);
        return Unit.Value;
    }
}
