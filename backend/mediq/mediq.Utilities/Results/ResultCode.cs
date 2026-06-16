using System.Runtime.Serialization;

namespace mediq.Utilities.Results;

[DataContract(Namespace = "")]
public class ResultCode(ResultType resultType, int errorCode = 0, string? messageText = null)
{
    public ResultCode() : this(ResultType.Error) { }

    [DataMember]
    public ResultType ResultType { get; private set; } = resultType;

    [DataMember(EmitDefaultValue = false)]
    public int ErrorCode { get; private set; } = errorCode;

    [DataMember(EmitDefaultValue = false)]
    public string MessageText { get; private set; } = messageText ?? string.Empty;
}
