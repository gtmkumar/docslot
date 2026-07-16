using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Auth;
using Microsoft.Extensions.Logging;

namespace mediq.Application.Features.Auth.PasswordReset;

/// <summary>
/// Self-service "forgot password" (<c>POST /api/v1/auth/forgot-password</c>, anonymous). ANTI-ENUMERATION: the
/// handler ALWAYS returns <c>Requested=true</c> — it mints + dispatches a reset token ONLY for a live account
/// that can authenticate, but the response is identical whether or not the email exists. The one-time link is
/// handed to the (offline-by-default) notifier; it is never returned in the response and never logged.
/// </summary>
public sealed record ForgotPasswordCommand(ForgotPasswordRequest Request)
    : ICommand<ForgotPasswordResult>, IDoNotCacheResponse;   // never replay-cache a credential-minting request

public sealed class ForgotPasswordValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordValidator()
    {
        RuleFor(x => x.Request.Email).NotEmpty().EmailAddress().MaximumLength(255);
    }
}

public sealed class ForgotPasswordCommandHandler(
    IUserRepository users, IPasswordResetRepository resets, IPasswordResetTokenFactory tokens,
    IPasswordResetNotifier notifier, IAuditTrailWriter audit, ILogger<ForgotPasswordCommandHandler> logger,
    ICurrentUserContext ctx, IClock clock)
    : ICommandHandler<ForgotPasswordCommand, ForgotPasswordResult>
{
    public async Task<ForgotPasswordResult> Handle(ForgotPasswordCommand command, CancellationToken ct)
    {
        var email = command.Request.Email.Trim();
        var user = await users.GetByEmailAsync(email, ct);

        // Mint ONLY for a live account that can actually authenticate (active, not deleted, has a password).
        // Every other case returns the SAME acknowledgement below — no enumeration signal.
        if (user is not null && user.CanAuthenticate)
        {
            var (token, tokenHash) = tokens.Create();
            var expiresAt = clock.UtcNow.Add(PasswordResetPolicy.Ttl);

            var tokenId = await resets.RequestAsync(user.UserId, tokenHash, ctx.IpAddress, expiresAt, ct);

            // Audit the MINT (internal trail — never an attacker-visible surface). No token/hash recorded.
            await audit.RecordAsync(new AuditEntry(
                "request_password_reset", "user", user.UserId, user.Email, user.UserId, null,
                ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
                ChangeSummary: "Self-service password reset requested", Purpose: "security"), ct);

            // Hand the one-time link to the (offline-by-default) notifier. Advisory — a failure never fails the request.
            await PasswordResetNotify.AdvisoryAsync(notifier, logger,
                new PasswordResetNotification(user.UserId, user.Email, token, expiresAt, IsAdminInitiated: false), ct);

            _ = tokenId;
        }

        // ALWAYS the same response — the caller cannot tell whether the email mapped to an account.
        return new ForgotPasswordResult(true);
    }
}
