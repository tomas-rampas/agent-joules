using System.Text.Json;
using System.Text.Json.Serialization;

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
}

/// <summary>
/// Reads and writes <see cref="EnergyMethod"/> as <c>"counter"</c> or <c>"sampling"</c> whatever the caller's naming policy;
/// a number or an unknown string is a <see cref="JsonException"/>. Apply it on the property as well as the type, because
/// a <see cref="JsonStringEnumConverter"/> in the caller's options outranks the attribute on the type.
/// </summary>
public sealed class EnergyMethodJsonConverter() : JsonStringEnumConverter<EnergyMethod>(namingPolicy: null, allowIntegerValues: false);