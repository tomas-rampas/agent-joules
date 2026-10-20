using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentJoules.Core.Labels;

/// <summary>
/// Reads and writes <see cref="ReadingFlags"/> as a JSON array of the vocabulary strings of <see cref="ReadingFlagNames"/>
/// (for example <c>["below-baseline","throttled"]</c>; an empty array for <see cref="ReadingFlags.None"/>), whatever the
/// caller's naming policy or enum converter. Reading rejects a non-array token, a non-string element, an unknown name and
/// a duplicate name with a <see cref="JsonException"/>.
/// </summary>
public sealed class ReadingFlagsJsonConverter : JsonConverter<ReadingFlags>
{
    /// <inheritdoc />
    public override ReadingFlags Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"The reading flags must be a JSON array of strings, not {reader.TokenType}.");
        }

        ReadingFlags flags = ReadingFlags.None;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return flags;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"A reading flag must be a string, not {reader.TokenType}.");
            }

            string name = reader.GetString()!;
            if (!ReadingFlagNames.TryParse(name, out ReadingFlags flag))
            {
                throw new JsonException($"Unknown reading flag \"{TextEcho.ForMessage(name)}\".");
            }

            if (flags.HasFlag(flag))
            {
                throw new JsonException($"Duplicate reading flag \"{TextEcho.ForMessage(name)}\".");
            }

            flags |= flag;
        }

        throw new JsonException("The reading flags array ended unexpectedly.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ReadingFlags value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartArray();
        foreach (string name in ReadingFlagNames.ToStrings(value))
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
    }
}