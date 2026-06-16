using System.Net;
using mediq.Utilities.ApiResponse.IResponseUtil;

namespace mediq.Utilities.ApiResponse;

public static class ResponseExtensions
{
    public static IActionResult ToHttpResponse(this IResponse response)
        => BuildResult(response, hasData: true);

    public static IActionResult ToHttpResponse<TModel>(this ISingleResponse<TModel> response)
        => BuildResult(response, hasData: response.Data is not null);

    public static IActionResult ToHttpResponse<TModel>(this IListResponse<TModel> response)
        => BuildResult(response, hasData: response.Data is not null);

    private static IActionResult BuildResult(IResponse response, bool hasData)
    {
        var status = response.Status
            ? HttpStatusCode.OK
            : (hasData ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound);

        return new ObjectResult(response) { StatusCode = (int)status };
    }
}
