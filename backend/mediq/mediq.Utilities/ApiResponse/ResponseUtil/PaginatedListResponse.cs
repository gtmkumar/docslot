using System.Runtime.Serialization;
using mediq.Utilities.ApiResponse.IResponseUtil;
using mediq.Utilities.Common;

namespace mediq.Utilities.ApiResponse.ResponseUtil;

[DataContract]
public class PaginatedListResponse<TModel> : IPaginatedListResponse<TModel>
{
    [DataMember(EmitDefaultValue = false)]
    public Message? Message { get; set; }

    [DataMember]
    public bool Status { get; set; }

    [DataMember(EmitDefaultValue = false)]
    public PaginatedList<TModel>? Data { get; set; }
}
