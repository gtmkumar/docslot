using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using mediq.SharedDataModel.Json;

namespace mediq.SharedDataModel.Docslot.Dashboard.Enums;

/// <summary>
/// UI / communication language.
/// <para>
/// Backs <c>docslot.patients.preferred_language</c> (<c>VARCHAR(10) NOT NULL DEFAULT 'en'</c>),
/// <c>docslot.conversations.detected_language</c>, and elements of
/// <c>docslot.doctors.languages_spoken</c> (<c>VARCHAR(10)[]</c>).
/// </para>
/// <para>
/// IMPORTANT: Unlike <see cref="BookingStatus"/>/<see cref="BookingSource"/>/<see cref="Gender"/>,
/// these columns have NO CHECK constraint — they are free <c>VARCHAR(10)</c> ISO-639-1
/// codes. This enum captures only the two officially supported bilingual codes
/// (en/hi per CLAUDE.md). Treat unknown DB values defensively: callers MUST tolerate
/// a raw string that does not map to a member rather than throwing. For storage of
/// arbitrary spoken-language arrays, keep the raw <c>string</c>/<c>string[]</c>;
/// use this enum only for the supported UI language pair.
/// </para>
/// </summary>
[DataContract]
[JsonConverter(typeof(EnumMemberJsonConverter))]
public enum Language
{
    /// <summary>English (default).</summary>
    [EnumMember(Value = "en")]
    English,

    /// <summary>Hindi.</summary>
    [EnumMember(Value = "hi")]
    Hindi
}
