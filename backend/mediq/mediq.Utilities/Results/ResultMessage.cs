using System.Runtime.Serialization;

namespace mediq.Utilities.Results;

[DataContract(Namespace = "")]
public enum ResultMessage
{
    [EnumMember(Value = "Success")]
    SuccessMessage,

    [EnumMember(Value = "Error")]
    ErrorMessage,

    [EnumMember(Value = "ExceptionDuringOperation")]
    ExceptionMessage
}
