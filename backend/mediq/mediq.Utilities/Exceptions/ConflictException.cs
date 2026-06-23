namespace mediq.Utilities.Exceptions;

/// <summary>
/// Thrown when a request conflicts with the current state of a resource — e.g. a separation-of-duties
/// (SoD) rule prevents holding two incompatible roles at once, or a uniqueness rule is violated by a
/// business operation. Maps to HTTP 409 Conflict.
/// </summary>
public sealed class ConflictException : AppExceptionBase
{
    private const string DefaultTitle = "Conflict";

    public ConflictException(string message)
        : base(DefaultTitle, message) { }

    public ConflictException(string message, Exception? innerException)
        : base(DefaultTitle, message, innerException) { }
}
