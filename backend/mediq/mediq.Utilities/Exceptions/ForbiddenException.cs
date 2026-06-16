namespace mediq.Utilities.Exceptions;

/// <summary>
/// Thrown when an authenticated caller is not permitted to perform an action
/// (authorization failure, not authentication). Maps to HTTP 403 Forbidden.
/// Use this in preference to UnauthorizedAccessException for authz failures so
/// clients receive 403 rather than 401.
/// </summary>
public sealed class ForbiddenException : AppExceptionBase
{
    private const string DefaultTitle = "Forbidden";

    public ForbiddenException(string message)
        : base(DefaultTitle, message) { }

    public ForbiddenException(string message, Exception? innerException)
        : base(DefaultTitle, message, innerException) { }
}
