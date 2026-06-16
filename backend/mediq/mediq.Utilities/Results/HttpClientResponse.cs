namespace mediq.Utilities.Results;

public class HttpClientResponse<TData>
{
    public HttpClientMessage? Message { get; set; }
    public bool Status { get; set; }
    public TData? Data { get; set; }
}

public class HttpClientResponse : HttpClientResponse<string>
{
}

public abstract class HttpClientMessage
{
    public int ErrorTypeCode { get; set; }
    public object? ErrorMessage { get; set; }
    public string? ResponseMessage { get; set; }
}
