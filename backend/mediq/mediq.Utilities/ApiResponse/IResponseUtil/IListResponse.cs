namespace mediq.Utilities.ApiResponse.IResponseUtil;

public interface IListResponse<TModel> : IResponse
{
    IEnumerable<TModel>? Data { get; set; }
}
