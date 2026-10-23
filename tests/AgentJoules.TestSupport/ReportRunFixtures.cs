using System.Globalization;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Render;
using AgentJoules.Reporting.Results;

namespace AgentJoules.TestSupport;

/// <summary>
/// The builders the report tests share (AJ-082 to AJ-087, REQ-016): one <see cref="StepReading"/> builder with a default for every metric, the run-file wrapper that gives a document the file name the
/// aggregator checks, and the list of words no report member or report text may carry because they name a figure across tiers. Documents are built in memory; the committed fixture files are never read.
/// </summary>
public static class ReportRunFixtures
{
    /// <summary>The start of the run, formatted to the second, that every fixture file name carries unless a test gives another.</summary>
    public const string Stamp = "20261023T093000Z";

    /// <summary>The task of the steps <see cref="Step"/> builds unless a test gives another.</summary>
    public const string TaskName = "summarise";

    /// <summary>The agent of the steps <see cref="Step"/> builds unless a test gives another.</summary>
    public const string Planner = "Planner";

    /// <summary>The words of a figure across tiers (a ratio, a ranking, a combined or overall value, a leader); no report member name and no rendered text may contain one.</summary>
    public static readonly IReadOnlyList<string> CrossTierWords = ["ratio", "rank", "best", "winner", "combined", "overall", "versus", "leader"];

    /// <summary>The file name of a run file: <c>&lt;stamp&gt;-&lt;tier&gt;[-baseline].json</c>.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="variant">Whether the file is the primary or the baseline file.</param>
    /// <param name="stamp">The start of the run formatted as <c>yyyyMMddTHHmmssZ</c>.</param>
    /// <returns>The name.</returns>
    public static string NameOf(Tier tier, RunVariant variant, string stamp = Stamp) =>
        $"{stamp}-{TierOptions.NameOf(tier)}{(variant == RunVariant.Baseline ? "-baseline" : string.Empty)}.json";

    /// <summary>
    /// A workflow step with a value for every metric: measured 1000 ms latency, 250 ms to the first token, 120 and 80 tokens, a not-configured cost; on a measured tier 60 J net, 90 W average and 110 W peak by the
    /// counter, on a hosted tier N/A (<c>hosted-tier</c>) for the energy and no method.
    /// </summary>
    /// <param name="tier">The tier of the step.</param>
    /// <param name="task">The task name.</param>
    /// <param name="repeat">The repeat number, from 1.</param>
    /// <param name="agent">The agent name.</param>
    /// <param name="modelId">The model ID.</param>
    /// <param name="outcome">The outcome.</param>
    /// <param name="net">The net energy, or null for the default.</param>
    /// <param name="inputTokens">The prompt tokens, or null for the default.</param>
    /// <param name="latency">The latency, or null for the default.</param>
    /// <param name="cost">The cost, or null for N/A <c>cost-not-configured</c>.</param>
    /// <param name="quality">The quality, or null for none.</param>
    /// <param name="flags">The reading flags.</param>
    /// <param name="method">The energy method of a measured tier, or null for the counter.</param>
    /// <param name="judgedAgent">The judged agent of a judge reading, or null.</param>
    /// <param name="gross">The gross energy, or null for the default.</param>
    /// <param name="avgW">The average power, or null for the default.</param>
    /// <param name="peakW">The peak power, or null for the default.</param>
    /// <param name="outputTokens">The completion tokens, or null for the default.</param>
    /// <param name="ttft">The time to the first token, or null for the default.</param>
    /// <returns>The reading.</returns>
    public static StepReading Step(
        Tier tier = Tier.Local,
        string task = TaskName,
        int repeat = 1,
        string agent = Planner,
        string modelId = "model-a",
        string outcome = StepOutcomes.Ok,
        Metric? net = null,
        Metric? inputTokens = null,
        Metric? latency = null,
        Metric? cost = null,
        Metric? quality = null,
        ReadingFlags flags = ReadingFlags.None,
        EnergyMethod? method = null,
        string? judgedAgent = null,
        Metric? gross = null,
        Metric? avgW = null,
        Metric? peakW = null,
        Metric? outputTokens = null,
        Metric? ttft = null)
    {
        bool hosted = TierKinds.IsHosted(tier);
        Metric hostedNa = Metric.NotApplicable("hosted-tier");
        Metric netJoules = net ?? (hosted ? hostedNa : Metric.Measured(60.0, "J"));
        DateTimeOffset start = StepReadingFixtures.StartUtc.AddSeconds(2 * repeat);

        return new StepReading(
            task: task,
            repeat: repeat,
            agent: agent,
            modelId: modelId,
            tier: tier,
            startUtc: start,
            endUtc: start.AddSeconds(1),
            outcome: outcome,
            latencyMs: latency ?? Metric.Measured(1000.0, "ms"),
            ttftMs: ttft ?? Metric.Measured(250.0, "ms"),
            inputTokens: inputTokens ?? Metric.Measured(120.0, "tokens"),
            outputTokens: outputTokens ?? Metric.Measured(80.0, "tokens"),
            grossJoules: gross ?? (hosted ? hostedNa : Metric.Measured((netJoules.Value ?? 0.0) + 30.0, "J")),
            netJoules: netJoules,
            avgW: avgW ?? (hosted ? hostedNa : Metric.Measured(90.0, "W")),
            peakW: peakW ?? (hosted ? hostedNa : Metric.Measured(110.0, "W")),
            cost: cost ?? Metric.NotApplicable("cost-not-configured"),
            retries: 0,
            waitMs: 0,
            flags: flags,
            method: hosted ? null : method ?? EnergyMethod.Counter,
            quality: quality,
            judgedAgent: judgedAgent);
    }

    /// <summary>One <see cref="Step"/> per value, repeats from 1; on a hosted tier the energy is N/A whatever the values are, so the values only count the repeats there.</summary>
    /// <param name="tier">The tier of the steps.</param>
    /// <param name="values">The net energy of each repeat.</param>
    /// <returns>The steps.</returns>
    public static StepReading[] NetSteps(Tier tier, params double[] values) =>
        [.. values.Select((value, index) => Step(tier, repeat: index + 1, net: TierKinds.IsHosted(tier) ? null : Metric.Measured(value, "J")))];

    /// <summary>A run file the aggregator accepts: the fixture document with the given steps, the tier, the start of the run taken from <paramref name="stamp"/> and the file name that goes with them.</summary>
    /// <param name="tier">The tier of the run.</param>
    /// <param name="steps">The workflow steps; each names <paramref name="tier"/>.</param>
    /// <param name="variant">Whether the file is the primary or the baseline file.</param>
    /// <param name="judge">The judge readings, or none.</param>
    /// <param name="tasks">The <c>run.tasks</c>, or the distinct tasks of the steps.</param>
    /// <param name="edit">An edit applied to the document last, or null.</param>
    /// <param name="stamp">The start of the run formatted as <c>yyyyMMddTHHmmssZ</c>.</param>
    /// <returns>The run file.</returns>
    public static SelectedRun RunOf(
        Tier tier,
        IReadOnlyList<StepReading> steps,
        RunVariant variant = RunVariant.Primary,
        IReadOnlyList<StepReading>? judge = null,
        IReadOnlyList<string>? tasks = null,
        Func<RunDocument, RunDocument>? edit = null,
        string stamp = Stamp)
    {
        RunDocument document = RunDocumentFixtures.Document(steps, judge ?? []);

        // The name of a run file starts with the start of the run formatted to the second, and Build checks that.
        document = document with { Run = document.Run with { Tier = tier, StartUtc = StartOf(stamp), Tasks = tasks ?? [.. steps.Select(step => step.Task).Distinct(StringComparer.Ordinal)] } };
        if (edit is not null)
        {
            document = edit(document);
        }

        return new SelectedRun(variant, NameOf(tier, variant, stamp), document);
    }

    /// <summary>
    /// Wraps a finished document as a run file: the start of the run is set from <paramref name="stamp"/> (a run file name starts with the start of the run formatted to the second, and the aggregator checks that)
    /// and the name follows from the tier of the document and the variant.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="variant">Whether the file is the primary or the baseline file.</param>
    /// <param name="stamp">The start of the run formatted as <c>yyyyMMddTHHmmssZ</c>.</param>
    /// <returns>The run file.</returns>
    public static SelectedRun Wrap(RunDocument document, RunVariant variant = RunVariant.Primary, string stamp = Stamp)
    {
        ArgumentNullException.ThrowIfNull(document);

        document = document with { Run = document.Run with { StartUtc = StartOf(stamp) } };

        return new SelectedRun(variant, NameOf(document.Run.Tier, variant, stamp), document);
    }

    private static DateTimeOffset StartOf(string stamp) =>
        DateTimeOffset.ParseExact(stamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}