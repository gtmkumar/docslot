using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Options;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.Utilities.Exceptions;
using Microsoft.Extensions.Options;

namespace mediq.Application.Features.Auth.Impersonation;

// ---- Begin impersonation (command) ---------------------------------------------------------------

/// <summary>
/// Opens an audited support impersonation session (issue #3) and mints a NEW access token carrying the
/// server-signed <c>impersonated_tenant</c> claim. Gated by <c>platform.users.impersonate</c> at the API;
/// the actor is always the authenticated principal (never the request body). The DB function
/// <c>platform.begin_impersonation()</c> writes the hash-chained audit row and re-checks the permission, so
/// the token is minted ONLY after an audited session exists.
/// </summary>
public sealed record BeginImpersonationCommand(BeginImpersonationRequest Request) : ICommand<ImpersonationResponse>;

public sealed class BeginImpersonationValidator : AbstractValidator<BeginImpersonationCommand>
{
    public BeginImpersonationValidator()
    {
        RuleFor(x => x.Request.TargetTenantId).NotEmpty();
        RuleFor(x => x.Request.Reason).NotEmpty().MaximumLength(200)
            .WithMessage("An impersonation reason is mandatory (audited).");
        RuleFor(x => x.Request.RefreshToken).NotEmpty();
        RuleFor(x => x.Request.TtlMinutes).InclusiveBetween(1, 480)
            .When(x => x.Request.TtlMinutes is not null)
            .WithMessage("TtlMinutes must be between 1 and 480 (8h).");
    }
}

public sealed class BeginImpersonationCommandHandler(
    IUserRepository users,
    IImpersonationRepository impersonation,
    ITokenService tokenService,
    ISessionStore sessions,
    IBrokerIdentityResolver brokerIdentity,
    ICurrentUserContext ctx,
    IClock clock,
    IOptions<JwtOptions> jwtOptions)
    : ICommandHandler<BeginImpersonationCommand, ImpersonationResponse>
{
    private const int DefaultTtlMinutes = 30;

    public async Task<ImpersonationResponse> Handle(BeginImpersonationCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var req = command.Request;

        // The caller must present a live (unrevoked, unexpired) refresh token bound to their session. The
        // actor is the session's user — bound server-side, NEVER taken from the request body.
        var session = await ResolveLiveSessionAsync(req.RefreshToken, now, ct);
        var user = await users.GetByIdAsync(session.UserId, ct);
        if (user is null || !user.CanAuthenticate)
            throw new InvalidRefreshTokenException();

        // Defense in depth (auditor INFO): the [RequirePermission] gate authorized the JWT principal, but the
        // operation acts for the refresh-token session's user. Bind them to the SAME principal so a
        // permission-holding token can never drive an impersonation session for a different user.
        if (ctx.UserId is null || ctx.UserId.Value != session.UserId)
            throw new ForbiddenException("Impersonation must be initiated by the authenticated principal.");

        var ttl = TimeSpan.FromMinutes(req.TtlMinutes ?? DefaultTtlMinutes);

        // Opens the AUDITED session at the DB. begin_impersonation() enforces platform.users.impersonate
        // (→ 403 on 42501) and writes the hash-chained audit row. We mint the impersonating token ONLY after
        // this returns — there is no token-without-audit path.
        var impersonationId = await impersonation.BeginAsync(
            user.UserId, req.TargetTenantId, req.Reason, req.TargetUserId, ttl, req.BreakGlass, ct);

        // Keep the actor's OWN active tenant; the impersonated_tenant claim ADDS the target. Cross-tenant PHI
        // still opens only because current_impersonated_tenant() finds the live session we just created.
        var brokerId = await brokerIdentity.ResolveBrokerIdAsync(user.UserId, ct);
        var access = tokenService.CreateAccessToken(
            user, session.ActiveTenantId, brokerId, impersonatedTenantId: req.TargetTenantId);
        var token = await RotateAsync(session.SessionId, user, session.ActiveTenantId, access, now, ct);

        return new ImpersonationResponse(token, impersonationId, req.TargetTenantId, now.Add(ttl));
    }

    private async Task<UserSessionRecord> ResolveLiveSessionAsync(string refreshToken, DateTime now, CancellationToken ct)
    {
        var presentedHash = tokenService.HashToken(refreshToken);
        var session = await sessions.FindByRefreshHashIncludingRevokedAsync(presentedHash, ct);
        if (session is null || session.RevokedAtUtc is not null || session.RefreshExpiresAtUtc < now)
            throw new InvalidRefreshTokenException();
        return session;
    }

    private async Task<TokenResponse> RotateAsync(
        Guid sessionId, Domain.Platform.User user, Guid? activeTenantId, AccessToken access, DateTime now, CancellationToken ct)
    {
        var (rawNew, newRefreshHash) = tokenService.CreateRefreshToken();
        var newAccessHash = tokenService.HashToken(access.Value);
        await sessions.RotateRefreshAsync(
            sessionId, newAccessHash, newRefreshHash,
            access.ExpiresAtUtc, now.AddDays(jwtOptions.Value.RefreshTokenDays), ct);
        return new TokenResponse(
            access.Value, rawNew, access.ExpiresInSeconds, user.UserId, activeTenantId, MfaRequired: user.MfaEnabled);
    }
}

// ---- End impersonation (command) -----------------------------------------------------------------

/// <summary>
/// Closes an active impersonation session (issue #3) and re-mints a CLEAN access token with NO
/// <c>impersonated_tenant</c> claim, so the support actor's cross-tenant scope drops immediately. The DB
/// function <c>platform.end_impersonation()</c> enforces self-close-or-<c>platform.users.impersonate</c>
/// (→ 403 on 42501) and writes the symmetric audit row; it is idempotent on an already-ended session.
/// </summary>
public sealed record EndImpersonationCommand(EndImpersonationRequest Request) : ICommand<TokenResponse>;

public sealed class EndImpersonationValidator : AbstractValidator<EndImpersonationCommand>
{
    public EndImpersonationValidator()
    {
        RuleFor(x => x.Request.ImpersonationId).NotEmpty();
        RuleFor(x => x.Request.RefreshToken).NotEmpty();
    }
}

public sealed class EndImpersonationCommandHandler(
    IUserRepository users,
    IImpersonationRepository impersonation,
    ITokenService tokenService,
    ISessionStore sessions,
    IBrokerIdentityResolver brokerIdentity,
    IClock clock,
    IOptions<JwtOptions> jwtOptions)
    : ICommandHandler<EndImpersonationCommand, TokenResponse>
{
    public async Task<TokenResponse> Handle(EndImpersonationCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var req = command.Request;

        var presentedHash = tokenService.HashToken(req.RefreshToken);
        var session = await sessions.FindByRefreshHashIncludingRevokedAsync(presentedHash, ct);
        if (session is null || session.RevokedAtUtc is not null || session.RefreshExpiresAtUtc < now)
            throw new InvalidRefreshTokenException();

        var user = await users.GetByIdAsync(session.UserId, ct);
        if (user is null || !user.CanAuthenticate)
            throw new InvalidRefreshTokenException();

        // Closes (or no-ops on already-ended) the session with a DB-enforced authorization check + audit row.
        await impersonation.EndAsync(req.ImpersonationId, user.UserId, ct);

        // Re-mint a clean token (no impersonated_tenant claim) and rotate, so the cleared scope is immediate.
        var brokerId = await brokerIdentity.ResolveBrokerIdAsync(user.UserId, ct);
        var access = tokenService.CreateAccessToken(user, session.ActiveTenantId, brokerId);
        var (rawNew, newRefreshHash) = tokenService.CreateRefreshToken();
        var newAccessHash = tokenService.HashToken(access.Value);
        await sessions.RotateRefreshAsync(
            session.SessionId, newAccessHash, newRefreshHash,
            access.ExpiresAtUtc, now.AddDays(jwtOptions.Value.RefreshTokenDays), ct);

        return new TokenResponse(
            access.Value, rawNew, access.ExpiresInSeconds, user.UserId, session.ActiveTenantId, MfaRequired: user.MfaEnabled);
    }
}
