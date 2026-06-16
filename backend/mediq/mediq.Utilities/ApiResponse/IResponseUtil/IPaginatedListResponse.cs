using mediq.Utilities.Common;

namespace mediq.Utilities.ApiResponse.IResponseUtil;

public interface IPaginatedListResponse<TModel> : IResponse
{
    PaginatedList<TModel>? Data { get; set; }
}
