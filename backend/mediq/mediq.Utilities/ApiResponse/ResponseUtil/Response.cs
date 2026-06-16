using System.Runtime.Serialization;
using mediq.Utilities.ApiResponse.IResponseUtil;

namespace mediq.Utilities.ApiResponse.ResponseUtil;

[DataContract]
public class Response : IResponse
{
    [DataMember(EmitDefaultValue = false)]
    public Message? Message { get; set; }

    [DataMember]
    public bool Status { get; set; }
}
