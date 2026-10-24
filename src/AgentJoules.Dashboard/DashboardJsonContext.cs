using System.Text.Json.Serialization;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;

namespace AgentJoules.Dashboard;

/// <summary>
/// The source-generated JSON context of the <c>data</c> member of a live payload (ADR-05, design 3.10): the options of the results context (camelCase names, nulls written and never omitted,
/// the converters of the flags, the tier, the energy method, the run mode and the run status), except that nothing is indented, because a server-sent event data line is one line. A metric
/// therefore serializes exactly as it does in a results file, <c>{"value":null,"label":"N/A","reason":"hosted-tier"}</c> for a value that does not exist, never as 0.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    Converters = [typeof(ReadingFlagsJsonConverter), typeof(TierJsonConverter), typeof(EnergyMethodJsonConverter), typeof(RunModeJsonConverter), typeof(RunStatusJsonConverter)])]
[JsonSerializable(typeof(RunStartedData))]
[JsonSerializable(typeof(BaselineData))]
[JsonSerializable(typeof(ProgressData))]
[JsonSerializable(typeof(StepStartedData))]
[JsonSerializable(typeof(StepProgressData))]
[JsonSerializable(typeof(StepCompletedData))]
[JsonSerializable(typeof(PowerData))]
[JsonSerializable(typeof(RunCompletedData))]
[JsonSerializable(typeof(SnapshotData))]
internal sealed partial class DashboardJsonContext : JsonSerializerContext;

/// <summary>The data of a <c>run-started</c> payload.</summary>
/// <param name="Run">The run section of the results file as it stands when the tier run starts.</param>
/// <param name="Environment">The environment section of the results file.</param>
/// <param name="PowerLimitW">The power limit of the measured GPU in watts, or null.</param>
internal sealed record RunStartedData(RunInfo Run, EnvironmentInfo Environment, double? PowerLimitW);

/// <summary>The data of a <c>baseline</c> payload.</summary>
/// <param name="Baseline">The baseline exactly as the results file records it.</param>
internal sealed record BaselineData(BaselineResult Baseline);

/// <summary>The data of a <c>progress</c> payload (all four numbers are 1-based).</summary>
/// <param name="Task">The task name, or null when the event named none (the page then says not-reported).</param>
/// <param name="TaskIndex">The task, from 1.</param>
/// <param name="TaskCount">The number of tasks.</param>
/// <param name="Repeat">The repeat, from 1.</param>
/// <param name="RepeatCount">The number of repeats.</param>
internal sealed record ProgressData(string? Task, int TaskIndex, int TaskCount, int Repeat, int RepeatCount);

/// <summary>The data of a <c>step-started</c> payload.</summary>
/// <param name="Step">Who the step belongs to.</param>
/// <param name="StartSeconds">The start of the step on the run timeline.</param>
/// <param name="Agents">Every agent with its status, cumulative values and running estimates after the event.</param>
/// <param name="ActiveAgents">The agents whose step is in flight.</param>
/// <param name="AgentsOmitted">The number of agents left out of <paramref name="Agents"/> because the state tracks a fixed number; 0 when none.</param>
internal sealed record StepStartedData(StepIdentity Step, double StartSeconds, IReadOnlyList<AgentData> Agents, IReadOnlyList<string> ActiveAgents, int AgentsOmitted);

/// <summary>The data of a <c>step-progress</c> payload.</summary>
/// <param name="Step">Who the step belongs to.</param>
/// <param name="OutputChunks">The streamed updates with text so far in the step.</param>
/// <param name="Agents">Every agent after the event, with the running estimates of the active ones.</param>
/// <param name="ActiveAgents">The agents whose step is in flight.</param>
/// <param name="AgentsOmitted">The number of agents left out of <paramref name="Agents"/>; 0 when none.</param>
internal sealed record StepProgressData(StepIdentity Step, int OutputChunks, IReadOnlyList<AgentData> Agents, IReadOnlyList<string> ActiveAgents, int AgentsOmitted);

/// <summary>The data of a <c>step-completed</c> payload.</summary>
/// <param name="Step">The reading exactly as the results file records it.</param>
/// <param name="Flags">The flags of the reading, plus <c>shared-window</c> when another step overlapped its window.</param>
/// <param name="Agents">Every agent after the event, with the cumulative values that include this step.</param>
/// <param name="ActiveAgents">The agents whose step is still in flight.</param>
/// <param name="AgentsOmitted">The number of agents left out of <paramref name="Agents"/>; 0 when none.</param>
internal sealed record StepCompletedData(StepReading Step, IReadOnlyList<string> Flags, IReadOnlyList<AgentData> Agents, IReadOnlyList<string> ActiveAgents, int AgentsOmitted);

/// <summary>The data of a <c>power</c> payload.</summary>
/// <param name="Watts">The sampled board power, labelled.</param>
/// <param name="SampleSeconds">The instant of the sample on the run timeline.</param>
/// <param name="Agents">Every agent after the event, with the running estimates of the active ones.</param>
/// <param name="ActiveAgents">The agents whose step is in flight.</param>
/// <param name="AgentsOmitted">The number of agents left out of <paramref name="Agents"/>; 0 when none.</param>
internal sealed record PowerData(Metric Watts, double SampleSeconds, IReadOnlyList<AgentData> Agents, IReadOnlyList<string> ActiveAgents, int AgentsOmitted);

/// <summary>The data of a <c>run-completed</c> payload.</summary>
/// <param name="Run">The run section of the written file.</param>
/// <param name="Totals">The totals of the written file.</param>
internal sealed record RunCompletedData(RunInfo Run, Totals Totals);

/// <summary>One agent of a payload: its status, the cumulative values of its completed steps and the running estimates of its step in flight.</summary>
/// <param name="Agent">The agent name.</param>
/// <param name="Configured">True for one of the agents of the configuration; false for any other, which the page marks with a fixed badge (a name can imitate a configured one).</param>
/// <param name="Status">The status: <c>waiting</c>, <c>running</c>, or the outcome of its latest step.</param>
/// <param name="CumulativeNetJoules">The net joules over its completed steps of this run, by the rule of the results totals.</param>
/// <param name="CumulativeInputTokens">The input tokens over its completed steps.</param>
/// <param name="CumulativeOutputTokens">The output tokens over its completed steps.</param>
/// <param name="RunningNetJoules">The running energy estimate of its step in flight, or null when it has none.</param>
/// <param name="RunningOutputChunks">The running count of streamed chunks of its step in flight, or null when none was reported.</param>
/// <param name="Flags">The flags of the running estimate (<c>shared-window</c> while another measured step is active).</param>
internal sealed record AgentData(
    string Agent,
    bool Configured,
    string Status,
    Metric CumulativeNetJoules,
    Metric CumulativeInputTokens,
    Metric CumulativeOutputTokens,
    Metric? RunningNetJoules,
    Metric? RunningOutputChunks,
    IReadOnlyList<string> Flags);

/// <summary>A completed step of a snapshot with its flags.</summary>
/// <param name="Step">The reading.</param>
/// <param name="Flags">Its flags, including <c>shared-window</c> where the rule of the results file applies.</param>
internal sealed record CompletedStepData(StepReading Step, IReadOnlyList<string> Flags);

/// <summary>The data of a <c>snapshot</c> payload: everything a page that connects now needs to draw the current state.</summary>
/// <param name="Run">The latest run started, or null.</param>
/// <param name="Environment">Its environment, or null.</param>
/// <param name="PowerLimitW">Its power limit, or null.</param>
/// <param name="Baseline">The baseline of the run, or null.</param>
/// <param name="Progress">The latest progress, or null.</param>
/// <param name="Completed">The completion of the run, or null while it runs.</param>
/// <param name="Agents">Every agent.</param>
/// <param name="ActiveAgents">The agents whose step is in flight.</param>
/// <param name="Steps">Every step completed so far, in completion order.</param>
/// <param name="Watts">The power of the last power event of the run, label kept (N/A included), or null before any; no history travels.</param>
/// <param name="SampleSeconds">The run-timeline instant of <paramref name="Watts"/>, or null before any.</param>
/// <param name="AgentsOmitted">The number of agents left out of <paramref name="Agents"/>; 0 when none.</param>
/// <param name="SnapshotDegraded">True when this snapshot is the smaller form written because the whole state could not be serialized; the page then says so.</param>
internal sealed record SnapshotData(
    RunInfo? Run,
    EnvironmentInfo? Environment,
    double? PowerLimitW,
    BaselineResult? Baseline,
    ProgressData? Progress,
    RunCompletedData? Completed,
    IReadOnlyList<AgentData> Agents,
    IReadOnlyList<string> ActiveAgents,
    IReadOnlyList<CompletedStepData> Steps,
    Metric? Watts,
    double? SampleSeconds,
    int AgentsOmitted,
    bool SnapshotDegraded = false);