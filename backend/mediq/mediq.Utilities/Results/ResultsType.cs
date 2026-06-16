using System.Runtime.Serialization;

namespace mediq.Utilities.Results;

[DataContract(Namespace = "")]
public enum ResultType
{
    [EnumMember(Value = "Success")]
    Success,

    [EnumMember(Value = "Error")]
    Error,

    [EnumMember(Value = "ExceptionDuringOperation")]
    ExceptionDuringOperation
}
