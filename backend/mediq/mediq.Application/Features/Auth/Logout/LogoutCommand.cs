using mediq.Application.Abstractions;
using mediq.Application.Cqrs;

namespace mediq.Application.Features.Auth.Logout;

/// <summary>
/// Revokes the session bound to the current access token (and, if supplied, the refresh token's session).
/// The access-token hash is taken from the request context (set by the API from the bearer token).
/// </summary>
public sealed record LogoutCommand(string AccessTokenHash, string? RefreshToken) : ICommand;

public sealed class LogoutCommandHandler(
    ITokenService tokenService,
    ISessionStore sessions,
    IAuditTrailWriter audit,
    ICurrentUserContext requestContext)
    : ICommandHandler<LogoutCommand>
{
    public async Task<Unit> Handle(LogoutCommand command, CancellationToken ct)
    {
        await sessions.RevokeByAccessHashAsync(command.AccessTokenHash, "logout", ct);

        if (!string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            var refreshHash = tokenService.HashToken(command.RefreshToken);
            var session = await sessions.FindByRefreshHashAsync(refreshHash, ct);
            if (session is not null)
                await sessions.RevokeAsync(session.SessionId, "logout", ct);
        }

        await audit.RecordAsync(new AuditEntry(
            "logout", "session", null, null, requestContext.UserId, requestContext.TenantId,
            requestContext.CorrelationId, requestContext.IpAddress, requestContext.UserAgent,
            Success: true, ChangeSummary: "Session revoked"), ct);

        return Unit.Value;
    }
}
