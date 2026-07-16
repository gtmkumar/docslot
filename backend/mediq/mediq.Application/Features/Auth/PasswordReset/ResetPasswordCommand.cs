using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Auth;

namespace mediq.Application.Features.Auth.PasswordReset;

/// <summary>
/// Redeem a reset token (<c>POST /api/v1/auth/reset-password</c>, anonymous — the token IS the authorization).
/// <c>consume_password_reset</c> (SECURITY DEFINER) validates the token is unused + unexpired, sets the new
/// password hash, clears must_change_password + lockout, marks the token used, and revokes all active
/// sessions — atomically. Any invalid / expired / already-used token surfaces as ONE generic 422 (no
/// enumeration). The response carries no secret.
/// </summary>
public sealed record ResetPasswordCommand(ResetPasswordRequest Request)
    : ICommand<ResetPasswordResult>, IDoNotCacheResponse;   // single-use; nothing to replay-cache

public sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Request.Token).NotEmpty();
        // Platform floor/ceiling for the new credential (the tenant policy min is not enforceable here — the
        // token is anonymous and carries no tenant context; the floor is the security guarantee).
        RuleFor(x => x.Request.NewPassword).NotEmpty().MinimumLength(8).MaximumLength(128);
    }
}

public sealed class ResetPasswordCommandHandler(
    IPasswordResetRepository resets, IPasswordResetTokenFactory tokens, IPasswordHasher hasher,
    IAuditTrailWriter audit, ICurrentUserContext ctx)
    : ICommandHandler<ResetPasswordCommand, ResetPasswordResult>
{
    public async Task<ResetPasswordResult> Handle(ResetPasswordCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var tokenHash = tokens.Hash(req.Token);            // look up by HASH — the plaintext is never stored
        var passwordHash = hasher.Hash(req.NewPassword);   // argon2id; the DB stores only the hash

        // consume_password_reset returns the user_id it reset; a bad/expired/used token → generic 422.
        var userId = await resets.ConsumeAsync(tokenHash, passwordHash, ct);

        // Audit the reset (the subject performed it via the token). No token/hash recorded.
        await audit.RecordAsync(new AuditEntry(
            "update", "user", userId, null, userId, null,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: "Password reset via one-time token; active sessions revoked", Purpose: "security"), ct);

        return new ResetPasswordResult(true);
    }
}
