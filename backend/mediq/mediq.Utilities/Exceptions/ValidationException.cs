namespace mediq.Utilities.Exceptions;

public sealed class ValidationException : AppExceptionBase
{
    private const string DefaultTitle = "Validation Failure";
    private const string DefaultMessage = "One or more validation errors occurred";

    public ValidationException()
        : base(DefaultTitle, DefaultMessage) { }

    public ValidationException(string message)
        : base(DefaultTitle, message) { }

    public ValidationException(string message, Exception? innerException)
        : base(DefaultTitle, message, innerException) { }

    public ValidationException(IReadOnlyDictionary<string, string[]> errorsDictionary)
        : base(DefaultTitle, DefaultMessage)
    {
        ArgumentNullException.ThrowIfNull(errorsDictionary);
        ErrorsDictionary = errorsDictionary;
    }

    public IReadOnlyDictionary<string, string[]>? ErrorsDictionary { get; }
}
