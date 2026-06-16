using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace mediq.SharedDataModel.Json;

/// <summary>
/// Serializes an enum as the string declared by its <see cref="EnumMemberAttribute"/> (e.g.
/// <c>no_show</c>, <c>walk_in</c>, <c>en</c>), falling back to the member name when none is set.
/// A plain <see cref="JsonStringEnumConverter"/> with a snake-case policy cannot do this — it would
/// emit <c>english</c> for <c>Language.English</c> instead of the canonical <c>en</c>.
/// <para>
/// Applied as a <c>[JsonConverter]</c> attribute on the enum type so BOTH the API and any client
/// (integration-test HttpClient included) honor it with default <see cref="JsonSerializerOptions"/>.
/// Reading is case-insensitive and accepts the EnumMember token, the raw member name, or a numeric
/// index (back-compat); output is always the string token.
/// </para>
/// </summary>
public sealed class EnumMemberJsonConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Inner<>).MakeGenericType(typeToConvert))!;

    private sealed class Inner<T> : JsonConverter<T> where T : struct, Enum
    {
        private static readonly ConcurrentDictionary<T, string> ToToken = new();
        private static readonly Dictionary<string, T> FromToken = new(StringComparer.OrdinalIgnoreCase);

        static Inner()
        {
            foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var value = (T)field.GetValue(null)!;
                var token = field.GetCustomAttribute<EnumMemberAttribute>()?.Value ?? field.Name;
                ToToken[value] = token;
                FromToken[token] = value;
                FromToken[field.Name] = value;
            }
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n))
                return (T)Enum.ToObject(typeof(T), n);

            var s = reader.GetString();
            if (s is not null && FromToken.TryGetValue(s, out var v)) return v;
            throw new JsonException($"Unknown {typeof(T).Name} value '{s}'.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(ToToken.TryGetValue(value, out var token) ? token : value.ToString());
    }
}
