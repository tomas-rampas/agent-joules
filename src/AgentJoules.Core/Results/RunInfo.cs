using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

using AgentJoules.Core.Labels;
using AgentJoules.Core.Runs;

namespace AgentJoules.Core.Results;

/// <summary>
/// The <c>run</c> object of the results file (REQ-015, REQ-017): who ran what, when, and where the run stands. The names are the camelCase names of
/// the skeleton in the prompt; <see cref="Status"/>, <see cref="Flags"/>, <see cref="StopReason"/> and <see cref="SelfJudged"/> are the additions of
/// design section 3.5. <see cref="Flags"/> holds only names of <see cref="RunFlagNames"/>, without a duplicate, and that is checked on construction, on <c>with</c> and on read.
/// </summary>
/// <param name="Id">The run identifier.</param>
/// <param name="Tier">The tier the file belongs to.</param>
/// <param name="Tasks">The names of the tasks the run covers, in run order.</param>
/// <param name="Mode">The execution mode of the run.</param>
/// <param name="Repeat">The number of repeats of each task.</param>
/// <param name="StartUtc">When the run started; the timestamp of the file name.</param>
/// <param name="ToolVersion">The version of the tool that wrote the file.</param>
/// <param name="GitCommit">The commit the tool was built from, or <c>unknown</c> (<see cref="UnknownCommit"/>) when the informational version has no <c>+</c> suffix; never null.</param>
/// <param name="Status">Where the run stands; <see cref="RunStatus.Running"/> on the first write.</param>
/// <param name="Flags">Run-level qualifiers (<see cref="RunFlagNames.All"/>: <c>partial-offload</c>, <c>non-publishable</c>); null or omitted means none, and <see cref="RunInfo.Flags"/> is then an empty list, written as <c>[]</c>.</param>
/// <param name="StopReason">Why a <see cref="RunStatus.Partial"/> or <see cref="RunStatus.Failed"/> run stopped, or null.</param>
/// <param name="SelfJudged">True when the judge model is also a workflow model, or null before the judge ran.</param>
/// <exception cref="ArgumentException"><paramref name="Flags"/> holds a null element, a name outside <see cref="RunFlagNames.All"/> or a name twice (the parameter name is <c>Flags</c>; the message names the offending value, bounded).</exception>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunInfo(
    [property: JsonPropertyOrder(1)] string Id,
    [property: JsonPropertyOrder(2), JsonConverter(typeof(TierJsonConverter))] Tier Tier,
    IReadOnlyList<string> Tasks,
    [property: JsonPropertyOrder(4), JsonConverter(typeof(RunModeJsonConverter))] RunMode Mode,
    [property: JsonPropertyOrder(5)] int Repeat,
    [property: JsonPropertyOrder(6)] DateTimeOffset StartUtc,
    string ToolVersion,
    string GitCommit,
    [property: JsonPropertyOrder(9), JsonConverter(typeof(RunStatusJsonConverter))] RunStatus Status,
    IReadOnlyList<string>? Flags = null,
    [property: JsonPropertyOrder(11)] string? StopReason = null,
    [property: JsonPropertyOrder(12)] bool? SelfJudged = null)
{
    /// <summary>The value of <see cref="GitCommit"/> when the build carries no commit: <c>unknown</c>, never null and never an empty string.</summary>
    public const string UnknownCommit = "unknown";

    private const string FlagsParameter = "Flags";
    private const string TasksParameter = "Tasks";
    private const string ToolVersionParameter = "ToolVersion";
    private const string GitCommitParameter = "GitCommit";

    /// <summary>
    /// The names of the tasks the run covers, in run order. No element is null; the list is a read-only copy. The order attributes keep the skeleton order of the file although this member is
    /// declared in the body.
    /// </summary>
    /// <exception cref="ArgumentNullException">The list or an element is null (the message names <c>run.tasks[i]</c>, the index only).</exception>
    [JsonPropertyOrder(3)]
    public IReadOnlyList<string> Tasks
    {
        get;
        init => field = CheckedTasks(value);
    } = CheckedTasks(Tasks);

    /// <summary>The version of the tool that wrote the file; not null and not blank.</summary>
    /// <exception cref="ArgumentException">The value is null, empty or white space (parameter name <c>ToolVersion</c>).</exception>
    [JsonPropertyOrder(7)]
    public string ToolVersion
    {
        get;
        init => field = RequireText(ToolVersionParameter, value);
    } = RequireText(ToolVersionParameter, ToolVersion);

    /// <summary>The commit the tool was built from, or <see cref="UnknownCommit"/>; not null and not blank.</summary>
    /// <exception cref="ArgumentException">The value is null, empty or white space (parameter name <c>GitCommit</c>).</exception>
    [JsonPropertyOrder(8)]
    public string GitCommit
    {
        get;
        init => field = RequireText(GitCommitParameter, value);
    } = RequireText(GitCommitParameter, GitCommit);

    /// <summary>
    /// Run-level qualifiers; an empty list when none apply, never null, so the file always holds an array. Every element is a name of <see cref="RunFlagNames"/> and none is repeated; the order is kept and
    /// the list is a read-only copy.
    /// </summary>
    /// <exception cref="ArgumentException">An element is null, unknown or repeated (parameter name <c>Flags</c>).</exception>
    [JsonPropertyOrder(10)]
    public IReadOnlyList<string> Flags
    {
        get;
        init => field = CheckedFlags(value);
    } = CheckedFlags(Flags);

    private static ReadOnlyCollection<string> CheckedTasks(IReadOnlyList<string>? tasks)
    {
        if (tasks is null)
        {
            throw NullElement(TasksParameter, "run.tasks must not be null.");
        }

        var copy = new List<string>(tasks.Count);
        for (int index = 0; index < tasks.Count; index++)
        {
            copy.Add(tasks[index] ?? throw NullElement(TasksParameter, $"run.tasks[{index}] must not be null."));
        }

        return new ReadOnlyCollection<string>(copy);
    }

    private static string RequireText(string parameterName, string? value) =>
        string.IsNullOrWhiteSpace(value) ? throw Rejected(parameterName, $"The run {parameterName} must not be null or blank.") : value;

    private static ReadOnlyCollection<string> CheckedFlags(IReadOnlyList<string>? flags)
    {
        var copy = new List<string>(flags?.Count ?? 0);
        if (flags is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string? flag in flags)
            {
                if (flag is null)
                {
                    throw Rejected(FlagsParameter, "The run flags must not hold a null element.");
                }

                if (!RunFlagNames.IsKnown(flag))
                {
                    throw Rejected(FlagsParameter, $"The run flag {TextEcho.ForMessage(flag)} is not one of {string.Join(", ", RunFlagNames.All)}.");
                }

                if (!seen.Add(flag))
                {
                    throw Rejected(FlagsParameter, $"The run flag {TextEcho.ForMessage(flag)} is listed more than once.");
                }

                copy.Add(flag);
            }
        }

        return new ReadOnlyCollection<string>(copy);
    }

    private static ArgumentException Rejected(string parameterName, string message) => new(message, parameterName);

    private static ArgumentNullException NullElement(string parameterName, string message) => new(parameterName, message);
}