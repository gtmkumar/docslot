using mediq.Utilities.Exceptions;

namespace mediq.Application.Features.Auth;

/// <summary>
/// Invalid email/password OR a disabled/locked account. Deliberately generic so the response never
/// reveals whether the email exists. Maps to 401 via the ExceptionHandler (UnauthorizedAccessException).
/// </summary>
public sealed class InvalidCredentialsException()
    : UnauthorizedAccessException("Invalid email or password.");

/// <summary>
/// Account locked after too many failed attempts. Maps to 403 (Forbidden, time-boxed). Uses the
/// Utilities <see cref="ForbiddenException"/> directly rather than subclassing (it is sealed).
/// </summary>
public static class AccountLocked
{
    public static ForbiddenException Until(DateTime untilUtc)
        => new($"Account is temporarily locked. Try again after {untilUtc:u}.");
}

/// <summary>The presented refresh token is unknown, expired, revoked, or reused. Maps to 401.</summary>
public sealed class InvalidRefreshTokenException()
    : UnauthorizedAccessException("The refresh token is invalid or expired.");
