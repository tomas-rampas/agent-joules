using System.Text.Json;
using System.Text.Json.Serialization;

using AgentJoules.Core.Tasks;

namespace AgentJoules.Core;

/// <summary>
/// The source-generated JSON context of the input documents that Core owns (ADR-05, design 3.2): the task files of <c>tasks/</c> today, the price
/// file later. Names are camelCase and a document is held to its type: a member the type does not have is refused, a duplicate member is refused, a
/// constructor parameter without a default must be present, and a null for a non-nullable member is refused.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(TaskDefinition))]
public sealed partial class CoreJsonContext : JsonSerializerContext;