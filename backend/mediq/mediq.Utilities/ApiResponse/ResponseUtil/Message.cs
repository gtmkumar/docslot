using System.Runtime.Serialization;

namespace mediq.Utilities.ApiResponse.ResponseUtil;

[DataContract]
public class Message
{
    [DataMember(EmitDefaultValue = false)]
    public ErrorMessageEnum ErrorTypeCode { get; set; }

    [DataMember(EmitDefaultValue = false)]
    public IReadOnlyDictionary<string, string[]>? ErrorMessage { get; set; }

    [DataMember(EmitDefaultValue = false)]
    public string? ResponseMessage { get; set; }
}
