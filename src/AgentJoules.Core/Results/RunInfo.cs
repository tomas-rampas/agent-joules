using System.Text.Json.Serialization;

using AgentJoules.Core.Runs;

namespace AgentJoules.Core.Results;

/// <summary>
/// The <c>run</c> object of the results file (REQ-015, REQ-017): who ran what, when, and where the run stands. The names are the camelCase names of
/// the skeleton in the prompt; <see cref="Status"/>, <see cref="Flags"/>, <see cref="StopReason"/> and <see cref="SelfJudged"/> are the additions of
/// design section 3.5. M3 hardens the record (invariants, schema) without renaming a member.
/// </summary>
/// <param name="Id">The run identifier.</param>
/// <param name="Tier">The tier the file belongs to.</param>
/// <param name="Tasks">The names of the tasks the run covers, in run order.</param>
/// <param name="Mode">The execution mode of the run.</param>
/// <param name="Repeat">The number of repeats of each task.</param>
/// <param name="StartUtc">When the run started; the timestamp of the file name.</param>
/// <param name="ToolVersion">The version of the tool that wrote the file.</param>
/// <param name="GitCommit">The commit the tool was built from, or null when none is known.</param>
/// <param name="Status">Where the run stands; <see cref="RunStatus.Running"/> on the first write.</param>
/// <param name="Flags">Run-level qualifiers (for example <c>partial-offload</c>, <c>non-publishable</c>); null or omitted means none, and <see cref="RunInfo.Flags"/> is then an empty list, written as <c>[]</c>.</param>
/// <param name="StopReason">Why a <see cref="RunStatus.Partial"/> or <see cref="RunStatus.Failed"/> run stopped, or null.</param>
/// <param name="SelfJudged">True when the judge model is also a workflow model, or null before the judge ran.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunInfo(
    string Id,
    [property: JsonConverter(typeof(TierJsonConverter))] Tier Tier,
    IReadOnlyList<string> Tasks,
    [property: JsonConverter(typeof(RunModeJsonConverter))] RunMode Mode,
    int Repeat,
    DateTimeOffset StartUtc,
    string ToolVersion,
    string? GitCommit,
    [property: JsonConverter(typeof(RunStatusJsonConverter))] RunStatus Status,
    IReadOnlyList<string>? Flags = null,
    [property: JsonPropertyOrder(2)] string? StopReason = null,
    [property: JsonPropertyOrder(3)] bool? SelfJudged = null)
{
    /// <summary>Run-level qualifiers; an empty list when none apply, never null, so the file always holds an array. The order attributes keep the skeleton order (flags, stopReason, selfJudged) although this member is declared in the body.</summary>
    [JsonPropertyOrder(1)]
    public IReadOnlyList<string> Flags { get; init; } = Flags ?? [];
}