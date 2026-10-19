using System.Text.Json.Serialization;

using AgentJoules.Core.Gpu;

namespace AgentJoules.Reporting.Json;

/// <summary>
/// The source-generated JSON context of the results model (ADR-05): camelCase names, indented, LF line endings, nulls written and never omitted.
/// AJ-035 registers <see cref="ProbeResult"/>; the run document types join it in M3.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ProbeResult))]
public sealed partial class AgentJoulesJsonContext : JsonSerializerContext;