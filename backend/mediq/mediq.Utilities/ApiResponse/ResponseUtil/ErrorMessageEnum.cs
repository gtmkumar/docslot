namespace mediq.Utilities.ApiResponse.ResponseUtil;

/// <summary>
/// Error type codes aligned with standard HTTP status codes (RFC 9110).
/// Clients can map these values directly to HTTP responses.
/// </summary>
public enum ErrorMessageEnum
{
    BadRequest = 400,
    UnAuthorized = 401,
    Forbidden = 403,
    NotFound = 404,
    Conflict = 409,
    ValidationFailed = 422,
    UpgradeRequired = 426,
    Error = 500
}
