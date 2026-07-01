using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Options;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.Utilities.Exceptions;
using Microsoft.Extensions.Options;

namespace mediq.Application.Features.Auth.SwitchTenant;

/// <summary>
/// Securely switches the active tenant for a multi-tenant user. The ONLY supported way to change the
/// tenant that scopes RLS/PHI: membership is validated server-side (same check as login) and a NEW access
/// token carrying the requested tenant claim is minted; the live session is rotated and rebound to that
/// tenant. A client can never change its tenant by sending a header.
/// </summary>
public sealed record SwitchTenantCommand(SwitchTenantRequest Request) : ICommand<TokenResponse>;

public sealed class SwitchTenantCommandHandler(
    IUserRepository users,
    ITenantRepository tenants,
    ITokenService tokenService,
    ISessionStore sessions,
    IAuditTrailWriter audit,
    IBrokerIdentityResolver brokerIdentity,
    ILoginSecurityPolicyGate policyGate,
    ICurrentUserContext requestContext,
    IClock clock,
    IOptions<JwtOptions> jwtOptions)
    : ICommandHandler<SwitchTenantCommand, TokenResponse>
{
    public async Task<TokenResponse> Handle(SwitchTenantCommand command, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var req = command.Request;

        // The caller must present a live (unrevoked, unexpired) refresh token bound to their session.
        var presentedHash = tokenService.HashToken(req.RefreshToken);
        var session = await sessions.FindByRefreshHashIncludingRevokedAsync(presentedHash, ct);
        if (session is null || session.RevokedAtUtc is not null || session.RefreshExpiresAtUtc < now)
            throw new mediq.Application.Features.Auth.InvalidRefreshTokenException();

        var user = await users.GetByIdAsync(session.UserId, ct);
        if (user is null || !user.CanAuthenticate)
            throw new mediq.Application.Features.Auth.InvalidRefreshTokenException();

        // FAIL-CLOSED membership check: the user MUST be an active member of the requested tenant.
        var memberships = await tenants.GetMembershipsAsync(user.UserId, ct);
        if (memberships.All(m => m.TenantId != req.TenantId))
        {
            await audit.RecordAsync(new AuditEntry(
                "switch_tenant_denied", "session", session.SessionId, req.TenantId.ToString(),
                user.UserId, req.TenantId, requestContext.CorrelationId, requestContext.IpAddress,
                requestContext.UserAgent, Success: false,
                ChangeSummary: $"Denied switch to tenant {req.TenantId} (not a member)"), ct);
            throw new ForbiddenException("You are not a member of the requested tenant.");
        }

        // Tenant security policy gate (issue #91) — switching INTO a tenant must satisfy ITS policy, exactly as
        // login does. Without this, switch-tenant would be a bypass: a session could mint a token scoped to a
        // hardened tenant while dodging its MFA tier / login-hours window / IP allow-list. Enforce against the
        // TARGET tenant, the current connection IP, and now. Same outcomes as login (403 mfa_enrollment_required /
        // 403 forbidden); the violation withholds the new token and leaves the current session untouched.
        try
        {
            await policyGate.EnforceAsync(user, req.TenantId, requestContext.IpAddress, now, ct);
        }
        catch (Exception ex) when (ex is MfaEnrollmentRequiredException or ForbiddenException)
        {
            await audit.RecordAsync(new AuditEntry(
                "switch_tenant_denied", "session", session.SessionId, req.TenantId.ToString(),
                user.UserId, req.TenantId, requestContext.CorrelationId, requestContext.IpAddress,
                requestContext.UserAgent, Success: false,
                ChangeSummary: $"Denied switch to tenant {req.TenantId} by security policy ({ex.Message})"), ct);
            throw;
        }

        // Mint a new access token carrying the NEW tenant claim; rotate + rebind the session.
        var brokerId = await brokerIdentity.ResolveBrokerIdAsync(user.UserId, ct);
        var access = tokenService.CreateAccessToken(user, req.TenantId, brokerId);
        var (rawNew, newRefreshHash) = tokenService.CreateRefreshToken();
        var newAccessHash = tokenService.HashToken(access.Value);

        await sessions.RotateRefreshWithTenantAsync(
            session.SessionId, req.TenantId, newAccessHash, newRefreshHash,
            access.ExpiresAtUtc, now.AddDays(jwtOptions.Value.RefreshTokenDays), ct);

        await audit.RecordAsync(new AuditEntry(
            "switch_tenant", "session", session.SessionId, req.TenantId.ToString(),
            user.UserId, req.TenantId, requestContext.CorrelationId, requestContext.IpAddress,
            requestContext.UserAgent, Success: true, ChangeSummary: $"Switched active tenant to {req.TenantId}"), ct);

        return new TokenResponse(
            access.Value, rawNew, access.ExpiresInSeconds, user.UserId, req.TenantId, MfaRequired: user.MfaEnabled);
    }
}
