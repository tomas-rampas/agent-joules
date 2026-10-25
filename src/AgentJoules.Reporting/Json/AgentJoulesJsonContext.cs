using System.Text.Json.Serialization;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;

namespace AgentJoules.Reporting.Json;

/// <summary>
/// The source-generated JSON context of the results model (ADR-05): camelCase names, indented, LF line endings, nulls written and never omitted. A document
/// is held to its type: a member the type does not have is refused, a duplicate member is refused, a constructor parameter without a default must be present
/// and a null for a non-nullable member is refused. The converters of the flags, the tier, the energy method, the run mode and the run status are registered
/// here as well as on the members, because they accept the exact strings of the schema only. Numbers are written by the serializer and are invariant by
/// construction. AJ-035 registered <see cref="ProbeResult"/>, AJ-055 registers <see cref="RunDocument"/>, AJ-109 registers <see cref="CalibrationDocument"/>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    Converters = [typeof(ReadingFlagsJsonConverter), typeof(TierJsonConverter), typeof(EnergyMethodJsonConverter), typeof(RunModeJsonConverter), typeof(RunStatusJsonConverter)])]
[JsonSerializable(typeof(ProbeResult))]
[JsonSerializable(typeof(RunDocument))]
[JsonSerializable(typeof(CalibrationDocument))]
public sealed partial class AgentJoulesJsonContext : JsonSerializerContext;