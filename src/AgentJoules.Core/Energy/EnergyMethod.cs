using System.Text.Json;
using System.Text.Json.Serialization;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Energy;

/// <summary>
/// How the gross energy of a window is obtained; the serialized strings are fixed by the results schema.
/// Serialized by <see cref="EnergyMethodJsonConverter"/>: the member names only, never a number.
/// </summary>
[JsonConverter(typeof(EnergyMethodJsonConverter))]
public enum EnergyMethod
{
    // Numbered from 1 so that default(EnergyMethod) is never a valid method.

    /// <summary>The difference of the GPU's cumulative energy counter, read at the window edges.</summary>
    [JsonStringEnumMemberName("counter")]
    Counter = 1,

    /// <summary>The integral of power samples over the window.</summary>
    [JsonStringEnumMemberName("sampling")]
    Sampling = 2,

    /// <summary>
    /// No energy is measured: the window belongs to a hosted tier (nvidia, nebius) whose GPU is not ours. Only <see cref="UnmeteredEnergyMeter"/>
    /// produces it, every metric of such a reading is N/A with the reason <c>hosted-tier</c>, and the selector, the calculator and the
    /// results file never use it: a step of a hosted tier stores no method.
    /// </summary>
    [JsonStringEnumMemberName("unmetered")]
    Unmetered = 3,
}

/// <summary>
/// Reads and writes <see cref="EnergyMethod"/> as exactly <c>"counter"</c>, <c>"sampling"</c> or <c>"unmetered"</c>, whatever the naming
/// policy of the caller. Unlike a <see cref="JsonStringEnumConverter"/> it accepts nothing else: a number, a padded or differently cased name,
/// a comma list or an unknown string is a <see cref="JsonException"/> with a bounded message. Apply it on the property as well as the type,
/// because a <see cref="JsonStringEnumConverter"/> in the options of the caller outranks the attribute on the type.
/// </summary>
public sealed class EnergyMethodJsonConverter : JsonConverter<EnergyMethod>
{
    /// <inheritdoc />
    public override EnergyMethod Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType != JsonTokenType.String ? throw new JsonException($"An energy method must be a JSON string, but the token is {reader.TokenType}.")
            : reader.ValueTextEquals("counter"u8) ? EnergyMethod.Counter
            : reader.ValueTextEquals("sampling"u8) ? EnergyMethod.Sampling
            : reader.ValueTextEquals("unmetered"u8) ? EnergyMethod.Unmetered
            : throw new JsonException($"An energy method must be counter, sampling or unmetered, but it is \"{TextEcho.ForMessage(reader.GetString() ?? string.Empty)}\".");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, EnergyMethod value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value switch
        {
            EnergyMethod.Counter => "counter",
            EnergyMethod.Sampling => "sampling",
            EnergyMethod.Unmetered => "unmetered",
            _ => throw new JsonException($"The energy method {(int)value} is not a defined energy method."),
        });
    }
}