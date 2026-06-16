using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using mediq.SharedDataModel.Json;

namespace mediq.SharedDataModel.Docslot.Dashboard.Enums;

/// <summary>
/// Gender of a patient/doctor.
/// <para>
/// Mirrors the CHECK constraint shared by <c>docslot.patients.gender</c> and
/// <c>docslot.doctors.gender</c> (database/03_docslot.sql):
/// <c>CHECK (gender IS NULL OR gender IN ('male', 'female', 'other', 'prefer_not_say'))</c>.
/// </para>
/// <para>
/// The column is nullable in the schema, so DTO fields of this type should be
/// declared <c>Gender?</c>. Wire values are the EXACT snake_case DB tokens.
/// </para>
/// </summary>
[DataContract]
[JsonConverter(typeof(EnumMemberJsonConverter))]
public enum Gender
{
    [EnumMember(Value = "male")]
    Male,

    [EnumMember(Value = "female")]
    Female,

    [EnumMember(Value = "other")]
    Other,

    [EnumMember(Value = "prefer_not_say")]
    PreferNotSay
}
