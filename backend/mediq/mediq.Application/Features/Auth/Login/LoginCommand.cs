using FluentValidation;
using mediq.Application.Cqrs;
using mediq.SharedDataModel.Docslot.Auth;

namespace mediq.Application.Features.Auth.Login;

/// <summary>
/// Authenticates a user and issues an access + refresh token pair. Records the attempt, enforces the
/// 5-failure lockout, and creates a hashed <c>user_sessions</c> row. NOT idempotency-guarded (login is
/// naturally safe to retry; a new session per call is acceptable).
/// </summary>
public sealed record LoginCommand(LoginRequest Request) : ICommand<TokenResponse>;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Request.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Request.Password).NotEmpty().MinimumLength(1);
    }
}
