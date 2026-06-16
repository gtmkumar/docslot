namespace mediq.Utilities.Exceptions;

public abstract class AppExceptionBase : Exception
{
    protected AppExceptionBase(string title, string message)
        : base(message)
    {
        Title = title;
    }

    protected AppExceptionBase(string title, string message, Exception? innerException)
        : base(message, innerException)
    {
        Title = title;
    }

    public string Title { get; }
}
