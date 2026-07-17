using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.Utilities.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace mediq.Application.Features.Auth.PasswordReset;

/// <summary>
/// Admin-initiated reset — mints a one-time reset link FOR a target user and returns it in the response body
/// (a LIVE CREDENTIAL the admin hands over out-of-band; never logged). The admin never handles plaintext: the
/// USER completes the reset via the self-service reset-password endpoint.
/// <para>
/// One command serves both routes. <c>RouteTenantId</c> non-null → the tenant route
/// (<c>/tenants/{tenantId}/users/{userId}/reset-password</c>, gated on <c>tenant.users.update</c>): the URL
/// tenant is validated to equal the signed-context tenant, and <c>admin_request_password_reset</c> enforces
/// the R3 no-escalation guard at the DB. <c>RouteTenantId</c> null → the platform route
/// (<c>/users/{userId}/reset-password</c>, gated on <c>platform.users.impersonate</c> — held only by
/// super_admin): the mint runs cross-tenant, and the SQL requires the actor to be super_admin (defence in
/// depth beyond the controller gate).
/// </para>
/// </summary>
public sealed record AdminResetPasswordCommand(Guid? RouteTenantId, Guid UserId)
    : ICommand<AdminResetPasswordResult>, IDoNotCacheResponse;   // returns a live link → never idempotency-cached

public sealed class AdminResetPasswordCommandHandler(
    IPasswordResetRepository resets, IPasswordResetTokenFactory tokens, IUserRepository users,
    IPasswordResetNotifier notifier, IAuditTrailWriter audit, ILogger<AdminResetPasswordCommandHandler> logger,
    ICurrentUserContext ctx, IClock clock, IConfiguration config)
    : ICommandHandler<AdminResetPasswordCommand, AdminResetPasswordResult>
{
    public async Task<AdminResetPasswordResult> Handle(AdminResetPasswordCommand command, CancellationToken ct)
    {
        var actorId = ctx.UserId ?? throw new ForbiddenException("An authenticated actor is required.");

        // Tenant route: the URL tenant must match the signed active tenant (never target another tenant).
        // Platform route: no tenant — the SQL requires super_admin.
        Guid? tenantId = null;
        if (command.RouteTenantId is { } routeTenant)
        {
            var active = ctx.TenantId
                ?? throw new ForbiddenException("A tenant context is required to reset a user's password.");
            if (routeTenant != active)
                throw new ForbiddenException("The URL tenant does not match your active tenant.");
            tenantId = active;
        }

        var (token, tokenHash) = tokens.Create();
        var expiresAt = clock.UtcNow.Add(PasswordResetPolicy.Ttl);

        // admin_request_password_reset (SECURITY DEFINER) enforces tenant.users.update + the R3 no-escalation
        // guard (tenant route) or super_admin (platform route) at the DB → 403 on 42501; unknown/non-member
        // target → generic 422.
        try
        {
            await resets.AdminRequestAsync(actorId, command.UserId, tokenHash, ctx.IpAddress, expiresAt, tenantId, ct);
        }
        catch (Exception ex) when (ex is ForbiddenException or BusinessRuleException)
        {
            // A DENIED reset (escalation guard tripped, or an unknown/non-member target) must still leave a
            // trail — otherwise an admin probing higher-privileged accounts is invisible. The mint's DB error
            // aborted the request transaction, but the audit writer commits on a DEDICATED connection, so this
            // record survives. No token material exists on the denied path; the target email is NOT looked up
            // (that read would hit the now-aborted request transaction). Rethrow to preserve the 403/422 status.
            await audit.RecordAsync(new AuditEntry(
                "update", "user", command.UserId, null, actorId, tenantId,
                ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: false,
                ChangeSummary: "Password reset denied", Purpose: "security"), ct);
            throw;
        }

        // Look up the target's email for the notifier + audit label (never the token).
        var target = await users.GetByIdAsync(command.UserId, ct);
        var targetEmail = target?.Email;

        await audit.RecordAsync(new AuditEntry(
            "update", "user", command.UserId, targetEmail, actorId, tenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: "Password reset link generated", Purpose: "security"), ct);

        // Advisory offline dispatch (email offline by default — the link is also returned in the body so the
        // admin can hand it over). A notifier failure never fails the mint.
        if (targetEmail is not null)
            await PasswordResetNotify.AdvisoryAsync(notifier, logger,
                new PasswordResetNotification(command.UserId, targetEmail, token, expiresAt, IsAdminInitiated: true), ct);

        return new AdminResetPasswordResult(PasswordResetLink.Build(config, token), expiresAt);
    }
}
