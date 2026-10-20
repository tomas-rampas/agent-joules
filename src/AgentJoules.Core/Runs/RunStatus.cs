using System.Text.Json;
using System.Text.Json.Serialization;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Runs;

/// <summary>
/// Where a run stands, written as <c>run.status</c> in every checkpoint of the results file (REQ-017, ADR-17): the first write is
/// <see cref="Running"/>, a normal end is <see cref="Completed"/>. <c>default(RunStatus)</c> is not a status. Serialized by
/// <see cref="RunStatusJsonConverter"/>: the member names only, never a number.
/// </summary>
[JsonConverter(typeof(RunStatusJsonConverter))]
public enum RunStatus
{
    // Numbered from 1 so that default(RunStatus) is never a valid status.

    /// <summary>The run has started and is writing checkpoints; a file that keeps this status after the process ended was cut off.</summary>
    [JsonStringEnumMemberName("running")]
    Running = 1,

    /// <summary>Every task and repeat finished and the final write was made.</summary>
    [JsonStringEnumMemberName("completed")]
    Completed = 2,

    /// <summary>A step failed and stopped the tier.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed = 3,

    /// <summary>The run stopped early on purpose (a budget stop) with the finished steps kept.</summary>
    [JsonStringEnumMemberName("partial")]
    Partial = 4,

    /// <summary>The run was interrupted by a host shutdown or a lost connection before it completed.</summary>
    [JsonStringEnumMemberName("incomplete")]
    Incomplete = 5,
}

/// <summary>
/// Reads and writes <see cref="RunStatus"/> as exactly <c>"running"</c>, <c>"completed"</c>, <c>"failed"</c>, <c>"partial"</c> or
/// <c>"incomplete"</c>, whatever the naming policy of the caller. Unlike a <see cref="JsonStringEnumConverter"/> it accepts nothing else: a number,
/// a padded or differently cased name, a comma list or an unknown string is a <see cref="JsonException"/> with a bounded message. Apply it on the
/// property as well as the type, because a <see cref="JsonStringEnumConverter"/> in the options of the caller outranks the attribute on the type.
/// </summary>
public sealed class RunStatusJsonConverter : JsonConverter<RunStatus>
{
    /// <inheritdoc />
    public override RunStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType != JsonTokenType.String ? throw new JsonException($"A run status must be a JSON string, but the token is {reader.TokenType}.")
            : reader.ValueTextEquals("running"u8) ? RunStatus.Running
            : reader.ValueTextEquals("completed"u8) ? RunStatus.Completed
            : reader.ValueTextEquals("failed"u8) ? RunStatus.Failed
            : reader.ValueTextEquals("partial"u8) ? RunStatus.Partial
            : reader.ValueTextEquals("incomplete"u8) ? RunStatus.Incomplete
            : throw new JsonException($"A run status must be running, completed, failed, partial or incomplete, but it is \"{TextEcho.ForMessage(reader.GetString() ?? string.Empty)}\".");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, RunStatus value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value switch
        {
            RunStatus.Running => "running",
            RunStatus.Completed => "completed",
            RunStatus.Failed => "failed",
            RunStatus.Partial => "partial",
            RunStatus.Incomplete => "incomplete",
            _ => throw new JsonException($"The run status {(int)value} is not a defined run status."),
        });
    }
}