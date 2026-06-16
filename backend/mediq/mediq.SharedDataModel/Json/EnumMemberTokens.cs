using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Serialization;

namespace mediq.SharedDataModel.Json;

/// <summary>
/// Resolves the canonical wire token of an enum member (its <see cref="EnumMemberAttribute"/> value, e.g.
/// <c>prefer_not_say</c> for <c>Gender.PreferNotSay</c>) WITHOUT round-tripping through JSON. The same
/// mapping <see cref="EnumMemberJsonConverter"/> uses for serialization, exposed for code paths (e.g. an
/// Application handler binding a strongly-typed enum to a snake_case DB token) that need the string but are
/// not serializing the whole DTO.
/// </summary>
public static class EnumMemberTokens
{
    private static readonly ConcurrentDictionary<Enum, string> Cache = new();

    /// <summary>The <see cref="EnumMemberAttribute"/> value for this member, falling back to the member name.</summary>
    public static string ToWireToken(this Enum value) =>
        Cache.GetOrAdd(value, static v =>
        {
            var field = v.GetType().GetField(v.ToString());
            return field?.GetCustomAttribute<EnumMemberAttribute>()?.Value ?? v.ToString();
        });
}
