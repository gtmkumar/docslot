namespace mediq.Utilities.Exceptions;

/// <summary>
/// Thrown when a business rule is violated (e.g. modifying a published price list,
/// exceeding a quota, state-machine guard). Maps to HTTP 422 Unprocessable Entity.
/// </summary>
public sealed class BusinessRuleException : AppExceptionBase
{
    private const string DefaultTitle = "Business Rule Violation";

    public BusinessRuleException(string message)
        : base(DefaultTitle, message) { }

    public BusinessRuleException(string message, Exception? innerException)
        : base(DefaultTitle, message, innerException) { }
}
