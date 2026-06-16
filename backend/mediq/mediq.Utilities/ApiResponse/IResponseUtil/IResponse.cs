using mediq.Utilities.ApiResponse.ResponseUtil;

namespace mediq.Utilities.ApiResponse.IResponseUtil;

public interface IResponse
{
    Message? Message { get; set; }
    bool Status { get; set; }
}
