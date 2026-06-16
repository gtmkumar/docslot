using mediq.Utilities.ApiResponse;
using mediq.Utilities.ApiResponse.ResponseUtil;

namespace mediq.Utilities.Attributes;

public static class CustomErrorResponse
{
    private const string UnauthorizedMarker = "UnAuthorized";

    public static IActionResult CustomError(ActionContext actionContext)
    {
        var errors = actionContext.ModelState
            .SelectMany(entry => entry.Value?.Errors ?? Enumerable.Empty<Microsoft.AspNetCore.Mvc.ModelBinding.ModelError>())
            .Select(error => error.ErrorMessage)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToList();

        var isUnauthorized = errors.Contains(UnauthorizedMarker);

        var response = new Response
        {
            Status = false,
            Message = new Message
            {
                ResponseMessage = isUnauthorized ? UnauthorizedMarker : string.Join(", ", errors),
                ErrorTypeCode = isUnauthorized ? ErrorMessageEnum.UnAuthorized : ErrorMessageEnum.BadRequest
            }
        };

        return response.ToHttpResponse();
    }
}
