using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.Application.Options;
using mediq.SharedDataModel.Docslot.Auth;
using Microsoft.Extensions.Options;

namespace mediq.Application.Features.Auth.Login;

/// <summary>
/// Login flow: resolve user → enforce lockout → verify password (bcrypt/argon2) → issue access+refresh →
/// persist hashed session → record attempt + audit. On failure, increment failed_login_count and lock
/// after the threshold. The generic <see cref="InvalidCredentialsException"/> hides whether the email exists.
/// </summary>
public sealed class LoginCommandHandler(
    IUserRepository users,
    ITenantRepository tenants,
    ITokenService tokenService,
    IPasswordHasher passwordHasher,
    ISessionStore sessions,
    ILoginAttemptService loginAttempts,
    IAuditTrailWriter audit,
    IBrokerIdentityResolver brokerIdentity,
    ILoginSecurityPolicyGate policyGate,
    ICurrentUserContext requestContext,
    IClock clock,
    IOptions<JwtOptions> jwtOptions,
    IOptions<AuthPolicyOptions> authPolicy)
    : ICommandHandler<LoginCommand, TokenResponse>
{
    public async Task<TokenResponse> Handle(LoginCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var now = clock.UtcNow;
        var policy = authPolicy.Value;

        var user = await users.GetByEmailAsync(req.Email, ct);

        // Uniform failure path for "no such user" and "wrong password" — never leak which.
        if (user is null)
        {
            await loginAttempts.RecordAsync(req.Email, requestContext.IpAddress, requestContext.UserAgent, false, "user_not_found", ct);
            throw new InvalidCredentialsException();
        }

        if (user.IsLockedOut(now))
        {
            await loginAttempts.RecordAsync(req.Email, requestContext.IpAddress, requestContext.UserAgent, false, "locked_out", ct);
            throw AccountLocked.Until(user.LockedUntil!.Value);
        }

        if (!user.CanAuthenticate || user.PasswordHash is null
            || !passwordHasher.Verify(req.Password, user.PasswordHash))
        {
            user.RegisterFailedLogin(now, policy.LockoutThreshold, TimeSpan.FromMinutes(policy.LockoutMinutes));
            await users.UpdateLoginStateAsync(user, ct);
            await loginAttempts.RecordAsync(req.Email, requestContext.IpAddress, requestContext.UserAgent, false, "bad_password", ct);
            await audit.RecordAsync(new AuditEntry(
                "login", "user", user.UserId, user.Email, user.UserId, req.TenantId,
                requestContext.CorrelationId, requestContext.IpAddress, requestContext.UserAgent,
                Success: false, ChangeSummary: "Failed login"), ct);
            throw new InvalidCredentialsException();
        }

        // Resolve the active tenant (validate membership if a specific tenant was requested).
        var activeTenantId = await ResolveActiveTenantAsync(user.UserId, req.TenantId, ct);

        // Tenant security policy gate (issue #91) — REAL block AFTER credentials, BEFORE any token/session:
        // 2FA-enrolment tier, login-hours window (IST, doctors optionally exempt), and IP allow-list. A
        // violation withholds the session entirely; the distinct 'mfa_enrollment_required' outcome maps to 403.
        try
        {
            await policyGate.EnforceAsync(user, activeTenantId, requestContext.IpAddress, now, ct);
        }
        catch (Exception ex) when (ex is mediq.Utilities.Exceptions.MfaEnrollmentRequiredException
                                      or mediq.Utilities.Exceptions.ForbiddenException)
        {
            await loginAttempts.RecordAsync(req.Email, requestContext.IpAddress, requestContext.UserAgent, false,
                ex is mediq.Utilities.Exceptions.MfaEnrollmentRequiredException ? "mfa_enrollment_required" : "policy_blocked", ct);
            await audit.RecordAsync(new AuditEntry(
                "login", "user", user.UserId, user.Email, user.UserId, activeTenantId,
                requestContext.CorrelationId, requestContext.IpAddress, requestContext.UserAgent,
                Success: false, ChangeSummary: $"Login blocked by security policy ({ex.Message})"), ct);
            throw;
        }

        // Resolve the caller's OWN broker identity (if any) for the IDOR-safe broker_id claim.
        var brokerId = await brokerIdentity.ResolveBrokerIdAsync(user.UserId, ct);

        // Issue tokens.
        var access = tokenService.CreateAccessToken(user, activeTenantId, brokerId);
        var (rawRefresh, refreshHash) = tokenService.CreateRefreshToken();
        var accessHash = tokenService.HashToken(access.Value);

        await sessions.CreateAsync(new SessionCreate(
            user.UserId, accessHash, refreshHash, activeTenantId,
            req.DeviceInfo ?? requestContext.UserAgent, requestContext.IpAddress,
            access.ExpiresAtUtc, now.AddDays(jwtOptions.Value.RefreshTokenDays)), ct);

        user.StampSuccessfulLogin(now, requestContext.IpAddress);
        await users.UpdateLoginStateAsync(user, ct);

        await loginAttempts.RecordAsync(req.Email, requestContext.IpAddress, requestContext.UserAgent, true, null, ct);
        await audit.RecordAsync(new AuditEntry(
            "login", "user", user.UserId, user.Email, user.UserId, activeTenantId,
            requestContext.CorrelationId, requestContext.IpAddress, requestContext.UserAgent,
            Success: true, ChangeSummary: "Successful login"), ct);

        return new TokenResponse(
            access.Value, rawRefresh, access.ExpiresInSeconds,
            user.UserId, activeTenantId, MfaRequired: user.MfaEnabled);
    }

    private async Task<Guid?> ResolveActiveTenantAsync(Guid userId, Guid? requestedTenantId, CancellationToken ct)
    {
        var memberships = await tenants.GetMembershipsAsync(userId, ct);

        if (requestedTenantId is { } requested)
            return memberships.Any(m => m.TenantId == requested) ? requested : null;

        // Prefer the primary tenant; else the first; else null (platform-level user / super_admin).
        return memberships.FirstOrDefault(m => m.IsPrimary)?.TenantId
            ?? memberships.FirstOrDefault()?.TenantId;
    }
}
