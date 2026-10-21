using System.Text.Json;
using System.Text.Json.Serialization;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Runs;

/// <summary>
/// The four tiers a run can measure, in the fixed order of the run (design ADR-13); the serialized strings are fixed by the results schema.
/// Serialized by <see cref="TierJsonConverter"/>: the member names only, never a number.
/// </summary>
[JsonConverter(typeof(TierJsonConverter))]
public enum Tier
{
    // Numbered from 1 so that default(Tier) is never a valid tier.

    /// <summary>A model served on the machine the meter reads (Ollama); energy is measured.</summary>
    [JsonStringEnumMemberName("local")]
    Local = 1,

    /// <summary>A model served on a rented GPU VM that the meter reads over NVML; energy is measured.</summary>
    [JsonStringEnumMemberName("cloudgpu")]
    CloudGpu = 2,

    /// <summary>The hosted NVIDIA endpoint; its GPU is not ours, so energy is N/A with the reason <c>hosted-tier</c>.</summary>
    [JsonStringEnumMemberName("nvidia")]
    Nvidia = 3,

    /// <summary>The hosted Nebius endpoint; its GPU is not ours, so energy is N/A with the reason <c>hosted-tier</c>.</summary>
    [JsonStringEnumMemberName("nebius")]
    Nebius = 4,
}

/// <summary>
/// Reads and writes <see cref="Tier"/> as exactly <c>"local"</c>, <c>"cloudgpu"</c>, <c>"nvidia"</c> or <c>"nebius"</c>, whatever the naming
/// policy of the caller. Unlike a <see cref="JsonStringEnumConverter"/> it accepts nothing else: a number, a padded or differently cased name,
/// a comma list or an unknown string is a <see cref="JsonException"/> with a bounded message. Apply it on the property as well as the type,
/// because a <see cref="JsonStringEnumConverter"/> in the options of the caller outranks the attribute on the type.
/// </summary>
public sealed class TierJsonConverter : JsonConverter<Tier>
{
    /// <inheritdoc />
    public override Tier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType != JsonTokenType.String ? throw new JsonException($"A tier must be a JSON string, but the token is {reader.TokenType}.")
            : reader.ValueTextEquals("local"u8) ? Tier.Local
            : reader.ValueTextEquals("cloudgpu"u8) ? Tier.CloudGpu
            : reader.ValueTextEquals("nvidia"u8) ? Tier.Nvidia
            : reader.ValueTextEquals("nebius"u8) ? Tier.Nebius
            : throw new JsonException($"A tier must be local, cloudgpu, nvidia or nebius, but it is \"{TextEcho.ForMessage(reader.GetString() ?? string.Empty)}\".");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Tier value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value switch
        {
            Tier.Local => "local",
            Tier.CloudGpu => "cloudgpu",
            Tier.Nvidia => "nvidia",
            Tier.Nebius => "nebius",
            _ => throw new JsonException($"The tier {(int)value} is not a defined tier."),
        });
    }
}