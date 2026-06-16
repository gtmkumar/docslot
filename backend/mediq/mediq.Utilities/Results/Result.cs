using System.Runtime.Serialization;

namespace mediq.Utilities.Results;

[DataContract(Namespace = "")]
public class Result<T>(ResultCode resultCode)
{
    public Result() : this(new ResultCode()) { }

    public Result(ResultCode resultCode, T userObject) : this(resultCode)
    {
        UserObject = userObject;
    }

    [DataMember]
    public ResultCode ResultCode { get; private set; } = resultCode ?? throw new ArgumentNullException(nameof(resultCode));

    [DataMember(EmitDefaultValue = false)]
    public T? UserObject { get; private set; }

    public bool HasSuccess => ResultCode.ResultType == ResultType.Success && UserObject is not null;
}
