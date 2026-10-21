using System.Text.Json;
using System.Text.Json.Serialization;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Runs;

/// <summary>
/// How a run schedules its metered calls (REQ-013, design 3.3): the value of <c>--mode</c>. The mode chooses the one process-wide meter gate and
/// nothing else, so the workflow topology is the same in both modes. The CLI default is <see cref="Serialized"/>; <c>default(RunMode)</c> is
/// not a mode. Serialized by <see cref="RunModeJsonConverter"/>: the member names only, never a number.
/// </summary>
[JsonConverter(typeof(RunModeJsonConverter))]
public enum RunMode
{
    // Numbered from 1 so that default(RunMode) is never a valid mode.

    /// <summary>One metered window at a time: parallel branches take turns at the gate, so no two windows overlap and the energy of a step is its own.</summary>
    [JsonStringEnumMemberName("serialized")]
    Serialized = 1,

    /// <summary>Metered windows may overlap: the gate admits everyone, and the run assembler flags overlapping steps as sharing a window.</summary>
    [JsonStringEnumMemberName("concurrent")]
    Concurrent = 2,
}

/// <summary>
/// Reads and writes <see cref="RunMode"/> as exactly <c>"serialized"</c> or <c>"concurrent"</c>, whatever the naming policy of the caller. Unlike a
/// <see cref="JsonStringEnumConverter"/> it accepts nothing else: a number, a padded or differently cased name, a comma list or an unknown string is
/// a <see cref="JsonException"/> with a bounded message. Apply it on the property as well as the type, because a
/// <see cref="JsonStringEnumConverter"/> in the options of the caller outranks the attribute on the type.
/// </summary>
public sealed class RunModeJsonConverter : JsonConverter<RunMode>
{
    /// <inheritdoc />
    public override RunMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType != JsonTokenType.String ? throw new JsonException($"A run mode must be a JSON string, but the token is {reader.TokenType}.")
            : reader.ValueTextEquals("serialized"u8) ? RunMode.Serialized
            : reader.ValueTextEquals("concurrent"u8) ? RunMode.Concurrent
            : throw new JsonException($"A run mode must be serialized or concurrent, but it is \"{TextEcho.ForMessage(reader.GetString() ?? string.Empty)}\".");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, RunMode value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value switch
        {
            RunMode.Serialized => "serialized",
            RunMode.Concurrent => "concurrent",
            _ => throw new JsonException($"The run mode {(int)value} is not a defined run mode."),
        });
    }
}