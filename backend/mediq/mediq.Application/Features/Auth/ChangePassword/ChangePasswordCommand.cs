using FluentValidation;
using mediq.Application.Abstractions;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Auth;
using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Auth.ChangePassword;

/// <summary>
/// Self-service password change for the authenticated caller. The new password must clear the tenant security
/// policy's <c>minPasswordLength</c> (issue #91) — a REAL enforcement point for the policy toggle (rejected → 422).
/// The response carries no secret, so it is safe to cache; nothing sensitive is returned.
/// </summary>
public sealed record ChangePasswordCommand(ChangePasswordRequest Request) : ICommand<ChangePasswordResult>;

public sealed record ChangePasswordResult(bool Changed);

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.Request.CurrentPassword).NotEmpty();
        // Absolute platform floor/ceiling; the TENANT policy min (which may be higher) is enforced in the handler.
        RuleFor(x => x.Request.NewPassword).NotEmpty().MinimumLength(8).MaximumLength(128);
    }
}

public sealed class ChangePasswordCommandHandler(
    IUserRepository users,
    IPasswordHasher passwordHasher,
    ITenantSecurityPolicyService securityPolicy,
    IAuditTrailWriter audit,
    ICurrentUserContext ctx)
    : ICommandHandler<ChangePasswordCommand, ChangePasswordResult>
{
    public async Task<ChangePasswordResult> Handle(ChangePasswordCommand command, CancellationToken ct)
    {
        var userId = ctx.UserId ?? throw new UnauthorizedAccessException("No authenticated user.");
        var req = command.Request;

        var user = await users.GetByIdAsync(userId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        // Re-verify the current password before allowing the change.
        if (user.PasswordHash is null || !passwordHasher.Verify(req.CurrentPassword, user.PasswordHash))
            throw new mediq.Utilities.Exceptions.ValidationException(new Dictionary<string, string[]>
            {
                ["CurrentPassword"] = ["The current password is incorrect."],
            });

        // Enforce the tenant policy's minimum length (issue #91). Falls back to the default floor when the
        // session has no active tenant (a platform user).
        var minLength = ctx.TenantId is { } tenantId
            ? (await securityPolicy.GetAsync(tenantId, ct)).MinPasswordLength
            : SecurityPolicy.Default.MinPasswordLength;

        if (req.NewPassword.Length < minLength)
            throw new mediq.Utilities.Exceptions.ValidationException(new Dictionary<string, string[]>
            {
                ["NewPassword"] = [$"Password must be at least {minLength} characters for this organization."],
            });

        await users.UpdatePasswordHashAsync(userId, passwordHasher.Hash(req.NewPassword), ct);

        await audit.RecordAsync(new AuditEntry(
            "update", "user", userId, user.Email, ctx.UserId, ctx.TenantId,
            ctx.CorrelationId, ctx.IpAddress, ctx.UserAgent, Success: true,
            ChangeSummary: "Password changed (self-service)", Purpose: "security"), ct);

        return new ChangePasswordResult(true);
    }
}
