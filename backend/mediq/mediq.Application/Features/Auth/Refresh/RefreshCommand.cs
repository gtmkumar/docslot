using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Options;
using mediq.SharedDataModel.Docslot.Auth;
using Microsoft.Extensions.Options;

namespace mediq.Application.Features.Auth.Refresh;

/// <summary>Rotates a refresh token and mints a new access token. Reuse of a revoked token is treated as theft.</summary>
public sealed record RefreshCommand(RefreshRequest Request) : ICommand<TokenResponse>;

public sealed class RefreshCommandHandler(
    IUserRepository users,
    ITokenService tokenService,
    ISessionStore sessions,
    IAuditTrailWriter audit,
    IBrokerIdentityResolver brokerIdentity,
    ICurrentUserContext requestContext,
    IClock clock,
    IOptions<JwtOptions> jwtOptions)
    : ICommandHandler<RefreshCommand, TokenResponse>
{
    public async Task<TokenResponse> Handle(RefreshCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var presentedHash = tokenService.HashToken(command.Request.RefreshToken);

        var session = await sessions.FindByRefreshHashIncludingRevokedAsync(presentedHash, ct);
        if (session is null)
            throw new mediq.Application.Features.Auth.InvalidRefreshTokenException();

        // REUSE-AFTER-REVOKE = theft signal. A previously-rotated/revoked refresh token is being replayed,
        // so fail closed: revoke EVERY session for this user (the attacker AND the legitimate user are logged
        // out; the user must re-authenticate). Then reject.
        if (session.RevokedAtUtc is not null)
        {
            await sessions.RevokeAllForUserAsync(session.UserId, "refresh_token_reuse_detected", ct);
            await audit.RecordAsync(new AuditEntry(
                "refresh_reuse_detected", "session", session.SessionId, null, session.UserId, session.ActiveTenantId,
                requestContext.CorrelationId, requestContext.IpAddress, requestContext.UserAgent,
                Success: false, ChangeSummary: "Revoked refresh token replayed — entire session chain revoked"), ct);
            throw new mediq.Application.Features.Auth.InvalidRefreshTokenException();
        }

        if (session.RefreshExpiresAtUtc < now)
            throw new mediq.Application.Features.Auth.InvalidRefreshTokenException();

        var user = await users.GetByIdAsync(session.UserId, ct);
        if (user is null || !user.CanAuthenticate)
            throw new mediq.Application.Features.Auth.InvalidRefreshTokenException();

        var brokerId = await brokerIdentity.ResolveBrokerIdAsync(user.UserId, ct);
        var access = tokenService.CreateAccessToken(user, session.ActiveTenantId, brokerId);
        var (rawNew, newRefreshHash) = tokenService.CreateRefreshToken();
        var newAccessHash = tokenService.HashToken(access.Value);

        // Rotation as a CHAIN, not an in-place overwrite: revoke the consumed session (so the old refresh
        // hash REMAINS in the table marked revoked) and create a fresh successor session. This is what makes
        // reuse-after-revoke detectable — a replay of the old token still finds its (now-revoked) row above.
        await sessions.RevokeAsync(session.SessionId, "rotated", ct);
        await sessions.CreateAsync(new SessionCreate(
            user.UserId, newAccessHash, newRefreshHash, session.ActiveTenantId,
            requestContext.UserAgent, requestContext.IpAddress,
            access.ExpiresAtUtc, now.AddDays(jwtOptions.Value.RefreshTokenDays)), ct);

        await audit.RecordAsync(new AuditEntry(
            "refresh", "session", session.SessionId, null, user.UserId, session.ActiveTenantId,
            requestContext.CorrelationId, requestContext.IpAddress, requestContext.UserAgent,
            Success: true, ChangeSummary: "Refresh token rotated (new session in chain)"), ct);

        return new TokenResponse(
            access.Value, rawNew, access.ExpiresInSeconds,
            user.UserId, session.ActiveTenantId, MfaRequired: user.MfaEnabled);
    }
}
