using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;

namespace AgentJoules.Reporting.Render;

/// <summary>
/// A run file the report uses: the file the selector chose for a tier and a variant, read through the strict context (AJ-082, REQ-016).
/// </summary>
/// <param name="Variant">Whether the file is the latest run file of its tier or its <c>-baseline</c> file.</param>
/// <param name="FileName">The name of the file without a directory (<c>Path.GetFileName</c> of the selected path); the report prints names only, never full paths.</param>
/// <param name="Document">The document read from the file.</param>
public sealed record SelectedRun(RunVariant Variant, string FileName, RunDocument Document);

/// <summary>
/// One value cell of a report row: the median of the repeats that have a value, with the range and the counts (AJ-082, REQ-016). Nothing in it is a sum, a mean or a comparison across tiers.
/// </summary>
/// <param name="Median">The median of the valued repeats, or null when no repeat has a value (N/A and not-reported are never 0).</param>
/// <param name="Min">The smallest valued repeat, or null.</param>
/// <param name="Max">The largest valued repeat, or null.</param>
/// <param name="N">The number of valued repeats.</param>
/// <param name="Total">The number of <c>ok</c> repeats of the row; the renderers print <c>n=N of Total</c> when <paramref name="N"/> is less than <paramref name="Total"/>.</param>
/// <param name="Label">The label of the valued repeats; with no valued repeat, N/A when any repeat is N/A and not-reported otherwise.</param>
/// <param name="Reasons">The distinct reasons of the repeats of a cell without a value, ordinal; empty when the cell has a value.</param>
/// <param name="Unit">The unit of the valued repeats, or null.</param>
/// <param name="Currency">The currency of the valued repeats, or null; a cost is never converted.</param>
public sealed record ReportCell(
    double? Median,
    double? Min,
    double? Max,
    int N,
    int Total,
    MetricLabel Label,
    IReadOnlyList<string> Reasons,
    string? Unit,
    string? Currency);

/// <summary>
/// One row of the report: the repeats of one agent on one task in one run file (AJ-082, REQ-016). The key is (<see cref="Tier"/>, <see cref="Variant"/>, <see cref="Task"/>, <see cref="Agent"/>); rows of
/// different tiers or variants are never merged or compared.
/// </summary>
/// <param name="Tier">The tier of the run file.</param>
/// <param name="Variant">Whether the row comes from the latest run file or the <c>-baseline</c> file of its tier.</param>
/// <param name="Task">The task name.</param>
/// <param name="Agent">The agent name.</param>
/// <param name="ModelIds">The distinct model IDs of the repeats, ordinal.</param>
/// <param name="Repeats">All repeats of the key, whatever their outcome.</param>
/// <param name="Cancelled">The repeats with the outcome <c>cancelled</c>.</param>
/// <param name="Errors">The repeats with the outcome <c>error</c>.</param>
/// <param name="Incomplete">The repeats with the outcome <c>incomplete</c>.</param>
/// <param name="GrossJoules">The gross energy cell.</param>
/// <param name="NetJoules">The net energy cell.</param>
/// <param name="AvgW">The average power cell.</param>
/// <param name="PeakW">The peak power cell.</param>
/// <param name="InputTokens">The prompt token cell.</param>
/// <param name="OutputTokens">The completion token cell.</param>
/// <param name="LatencyMs">The latency cell.</param>
/// <param name="TtftMs">The time-to-first-token cell.</param>
/// <param name="Cost">The cost cell; the currency is kept.</param>
/// <param name="Quality">The judged quality cell; the <c>model-judged</c> label is kept.</param>
/// <param name="Flags">The flags of the <c>ok</c> repeats united, plus the flags of the baseline of the file.</param>
public sealed record ReportRow(
    Tier Tier,
    RunVariant Variant,
    string Task,
    string Agent,
    IReadOnlyList<string> ModelIds,
    int Repeats,
    int Cancelled,
    int Errors,
    int Incomplete,
    ReportCell GrossJoules,
    ReportCell NetJoules,
    ReportCell AvgW,
    ReportCell PeakW,
    ReportCell InputTokens,
    ReportCell OutputTokens,
    ReportCell LatencyMs,
    ReportCell TtftMs,
    ReportCell Cost,
    ReportCell Quality,
    ReadingFlags Flags);

/// <summary>
/// The facts of one run file the report header shows (AJ-082, REQ-016): where it ran, what it ran on and how the run stands. The totals are those of the file, never recomputed and never summed across files.
/// </summary>
/// <param name="Tier">The tier of the file.</param>
/// <param name="Variant">Whether the file is the latest run file or the <c>-baseline</c> file of its tier.</param>
/// <param name="FileName">The file name without a directory.</param>
/// <param name="StartUtc">When the run started.</param>
/// <param name="Status">Where the run stands (<c>completed</c> or <c>partial</c> for a selected file).</param>
/// <param name="Mode">The execution mode of the run.</param>
/// <param name="GpuName">The GPU name, or null.</param>
/// <param name="DriverVersion">The driver version, or null.</param>
/// <param name="LocalRuntime">The local model runtime, or null.</param>
/// <param name="RuntimeVersion">The version of the local runtime, or null.</param>
/// <param name="GpuOffloadPercent">The share of the model held in GPU memory in percent (0 to 100), or null when the file states none (a hosted tier); null is never shown as 0.</param>
/// <param name="Precision">The model precision, or null.</param>
/// <param name="ReasoningMode">The reasoning mode of the tier, or null.</param>
/// <param name="PowerSource">The laptop's power source during the run: ac, battery or unknown, or null when none was asked.</param>
/// <param name="Models">The model each agent used, as the file lists them.</param>
/// <param name="JudgeModelIds">The distinct model IDs of the judge readings, ordinal.</param>
/// <param name="JudgedStepModelIds">The distinct model IDs of the workflow steps that have a judge reading (the steps that were sent to the judge), ordinal; empty when no step was judged.</param>
/// <param name="SelfJudged">True when the judge model is also a workflow model, or null before the judge ran.</param>
/// <param name="EnergyMethods">The distinct energy methods of the steps, in enum order.</param>
/// <param name="SamplingFallback">True when any <c>ok</c> step carries the <c>sampling-fallback</c> flag.</param>
/// <param name="RunFlags">The run-level flags of the file (<c>partial-offload</c>).</param>
/// <param name="Totals">The totals of the file.</param>
public sealed record TierReport(
    Tier Tier,
    RunVariant Variant,
    string FileName,
    DateTimeOffset StartUtc,
    RunStatus Status,
    RunMode Mode,
    string? GpuName,
    string? DriverVersion,
    string? LocalRuntime,
    string? RuntimeVersion,
    double? GpuOffloadPercent,
    string? Precision,
    string? ReasoningMode,
    string? PowerSource,
    IReadOnlyList<EnvironmentModel> Models,
    IReadOnlyList<string> JudgeModelIds,
    IReadOnlyList<string> JudgedStepModelIds,
    bool? SelfJudged,
    IReadOnlyList<EnergyMethod> EnergyMethods,
    bool SamplingFallback,
    IReadOnlyList<string> RunFlags,
    Totals Totals);

/// <summary>
/// What the renderers print (AJ-082, REQ-016): the run files in use, the tiers that were not run, the rows and the disclosure sentences. It holds no ratio, ranking or figure across tiers.
/// </summary>
/// <param name="Tiers">One report per run file, in tier order (local, cloudgpu, nvidia, nebius), the primary file before the baseline file.</param>
/// <param name="TiersNotRun">The known tiers without a primary run file, in tier order.</param>
/// <param name="Rows">The rows in the order of <paramref name="Tiers"/>, then task, then agent.</param>
/// <param name="Disclosures">The sentences every surface prints, in a fixed order, each at most once.</param>
public sealed record ReportModel(
    IReadOnlyList<TierReport> Tiers,
    IReadOnlyList<Tier> TiersNotRun,
    IReadOnlyList<ReportRow> Rows,
    IReadOnlyList<string> Disclosures);