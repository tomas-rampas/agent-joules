using System.Collections.ObjectModel;
using System.Globalization;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;

namespace AgentJoules.Reporting.Render;

/// <summary>
/// Aggregates the repeats of the run files a report uses into rows (AJ-082, REQ-016). A row is (tier, variant, task, agent) over the <c>ok</c> steps of one file; each value cell holds the median of the repeats
/// that have a value, with the range and <c>n</c>, and keeps their label, unit and currency. A cell whose repeats have no value is N/A or not-reported with a null value, never 0. Nothing is merged, summed,
/// averaged, ranked or compared across tiers or variants: every file gives its own rows and its own <see cref="TierReport"/>, and the totals are those of the file.
/// </summary>
public static class ReportAggregator
{
    /// <summary>The reason of every cell of a row that has no <c>ok</c> step.</summary>
    public const string NoOkStepReason = "no-ok-step";

    /// <summary>The reason of a cost cell whose repeats are in more than one currency; a cost is never converted.</summary>
    public const string MixedCurrencyReason = "mixed-currency";

    private const string NotAvailable = "N/A";
    private const string BaselineSuffix = "-baseline";
    // The name of the public parameter, for the checks that run in helpers; a field (not a constant) because the analyzer only accepts the parameter names of the method that throws.
    private static readonly string RunsParameter = "runs";
    private const string Extension = ".json";
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    // The order of TierOptions.KnownNames, as tiers.
    private static readonly Tier[] TierOrder = KnownTiers();

    /// <summary>
    /// Builds the report model over the run files in use. The tiers and the rows come in the order of <see cref="TierOptions.KnownNames"/>, the primary file before the baseline file of a tier; the tasks follow
    /// <c>run.tasks</c> (tasks it does not list come after, ordinal) and the agents their first appearance in <c>steps[]</c>.
    /// </summary>
    /// <param name="runs">The run files in use, at most one per tier and variant.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runs"/> or an element or document of it is null.</exception>
    /// <exception cref="ArgumentException">
    /// Two runs share a tier and a variant; a file name is not a run file name, disagrees with the tier of the document, the variant or the start of the run, or the run is not completed or partial or is
    /// <c>non-publishable</c> (what the selector guaranteed, checked again on the document that was read); a step names another tier than the run; or the valued repeats of a cell disagree in label or unit
    /// (which a validated file cannot do). Every message names <c>runs[i]</c> and the member, and for a step <c>Document.Steps[j]</c>; none echoes a name, a task, an agent or a value, and the parameter name is
    /// <c>runs</c>.
    /// </exception>
    public static ReportModel Build(IReadOnlyList<SelectedRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var seen = new HashSet<(Tier, RunVariant)>();
        var checkedRuns = new List<(SelectedRun Run, int Index)>(runs.Count);
        for (int index = 0; index < runs.Count; index++)
        {
            SelectedRun run = runs[index] ?? throw new ArgumentNullException(RunsParameter, $"runs[{index}] must not be null.");
            CheckRun(run, index);
            if (!seen.Add((run.Document.Run.Tier, run.Variant)))
            {
                throw new ArgumentException($"runs[{index}] has the tier and the variant of an earlier run; a report holds one file per tier and variant.", RunsParameter);
            }

            checkedRuns.Add((run, index));
        }

        (SelectedRun Run, int Index)[] ordered =
        [
            .. checkedRuns
                .OrderBy(entry => Array.IndexOf(TierOrder, entry.Run.Document.Run.Tier))
                .ThenBy(entry => (int)entry.Run.Variant),
        ];

        var tiers = new List<TierReport>(ordered.Length);
        var rows = new List<ReportRow>();
        foreach ((SelectedRun run, int index) in ordered)
        {
            tiers.Add(BuildTierReport(run));
            rows.AddRange(BuildRows(run, index));
        }

        Tier[] notRun = [.. TierOrder.Where(tier => !tiers.Any(report => report.Tier == tier && report.Variant == RunVariant.Primary))];

        return new ReportModel(tiers.AsReadOnly(), Array.AsReadOnly(notRun), rows.AsReadOnly(), BuildDisclosures(tiers));
    }

    private static Tier[] KnownTiers()
    {
        Dictionary<string, Tier> byName = Enum.GetValues<Tier>().ToDictionary(TierOptions.NameOf, StringComparer.Ordinal);

        return [.. TierOptions.KnownNames.Select(name => byName[name])];
    }

    private static void CheckRun(SelectedRun run, int index)
    {
        if (run.Document is null)
        {
            throw new ArgumentNullException(RunsParameter, $"runs[{index}].Document must not be null.");
        }

        if (!Enum.IsDefined(run.Variant) || !Enum.IsDefined(run.Document.Run.Tier))
        {
            throw new ArgumentException($"runs[{index}] has a variant or a document tier that is not defined.", RunsParameter);
        }

        if (!RunFileName.IsValid(run.FileName))
        {
            throw new ArgumentException($"runs[{index}].FileName is not a run file name; it is the name without a directory, as {RunFileName.Pattern} says.", RunsParameter);
        }

        // The name satisfies RunFileName.IsValid: <timestamp>-<tier>[-baseline].json.
        string stem = run.FileName[..^Extension.Length];
        bool baseline = stem.EndsWith(BaselineSuffix, StringComparison.Ordinal);
        string rest = stem[(stem.IndexOf('-', StringComparison.Ordinal) + 1)..];
        string tierName = baseline ? rest[..^BaselineSuffix.Length] : rest;
        if (!string.Equals(tierName, TierOptions.NameOf(run.Document.Run.Tier), StringComparison.Ordinal))
        {
            throw new ArgumentException($"runs[{index}].FileName names another tier than run.tier of the document.", RunsParameter);
        }

        if (baseline != (run.Variant == RunVariant.Baseline))
        {
            throw new ArgumentException($"runs[{index}].FileName has a -baseline suffix that does not agree with the variant.", RunsParameter);
        }

        // The tool names a run file after the start of the command (RunCommand: one startUtc for every file name and every run.startUtc), formatted to the second in UTC.
        RunInfo info = run.Document.Run;
        if (!run.FileName.StartsWith(info.StartUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new ArgumentException($"runs[{index}].FileName has another timestamp than run.startUtc.", RunsParameter);
        }

        // What the selector guaranteed from the header, checked again on the document that was read: a file replaced between the two reads must not reach a row.
        if (info.Status is not (RunStatus.Completed or RunStatus.Partial))
        {
            throw new ArgumentException($"runs[{index}].Document.Run.Status is not completed or partial.", RunsParameter);
        }

        if (info.Flags.Contains(RunFlagNames.NonPublishable, StringComparer.Ordinal))
        {
            throw new ArgumentException($"runs[{index}].Document.Run.Flags holds {RunFlagNames.NonPublishable}.", RunsParameter);
        }

        // A row is one tier; the judge readings carry the tier of the judge and are not checked here.
        for (int step = 0; step < run.Document.Steps.Count; step++)
        {
            if (run.Document.Steps[step].Tier != info.Tier)
            {
                throw new ArgumentException($"runs[{index}].Document.Steps[{step}].Tier differs from the run tier.", RunsParameter);
            }
        }
    }

    private static TierReport BuildTierReport(SelectedRun run)
    {
        RunDocument document = run.Document;
        RunInfo info = document.Run;
        EnvironmentInfo environment = document.Environment;
        IReadOnlyList<StepReading> steps = document.Steps;

        string[] judgeModels = [.. document.Judge.Select(reading => reading.ModelId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        EnergyMethod[] methods = [.. steps.Where(step => step.Method is not null).Select(step => step.Method!.Value).Distinct().Order()];
        bool samplingFallback = steps.Any(step => step.Outcome == StepOutcomes.Ok && step.Flags.HasFlag(ReadingFlags.SamplingFallback));

        return new TierReport(
            info.Tier,
            run.Variant,
            run.FileName,
            info.StartUtc,
            info.Status,
            info.Mode,
            environment.GpuName,
            environment.DriverVersion,
            environment.LocalRuntime,
            environment.RuntimeVersion,
            environment.Precision,
            environment.ReasoningMode,
            environment.PowerSource,
            environment.Models,
            Array.AsReadOnly(judgeModels),
            info.SelfJudged,
            Array.AsReadOnly(methods),
            samplingFallback,
            info.Flags,
            document.Totals);
    }

    // One pass over steps[] groups the steps by task and agent and notes where each agent first appears, so the rows cost O(S log S) however many distinct tasks and agents a file holds.
    private static List<ReportRow> BuildRows(SelectedRun run, int runIndex)
    {
        RunDocument document = run.Document;
        IReadOnlyList<StepReading> steps = document.Steps;

        var firstSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        var byTask = new Dictionary<string, Dictionary<string, List<int>>>(StringComparer.Ordinal);
        for (int index = 0; index < steps.Count; index++)
        {
            StepReading step = steps[index];
            firstSeen.TryAdd(step.Agent, firstSeen.Count);
            if (!byTask.TryGetValue(step.Task, out Dictionary<string, List<int>>? byAgent))
            {
                byAgent = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                byTask[step.Task] = byAgent;
            }

            if (!byAgent.TryGetValue(step.Agent, out List<int>? indexes))
            {
                indexes = [];
                byAgent[step.Agent] = indexes;
            }

            indexes.Add(index);
        }

        string[] listed = [.. document.Run.Tasks.Distinct(StringComparer.Ordinal)];
        string[] unlisted = [.. byTask.Keys.Except(listed, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        var rows = new List<ReportRow>();
        foreach (string task in listed.Concat(unlisted))
        {
            if (!byTask.TryGetValue(task, out Dictionary<string, List<int>>? agents))
            {
                continue;
            }

            foreach (string agent in agents.Keys.OrderBy(name => firstSeen[name]))
            {
                List<int> indexes = agents[agent];
                rows.Add(BuildRow(run, runIndex, task, agent, indexes, [.. indexes.Select(index => steps[index])]));
            }
        }

        return rows;
    }

    private static ReportRow BuildRow(SelectedRun run, int runIndex, string task, string agent, List<int> indexes, StepReading[] keyed)
    {
        StepReading[] ok = [.. keyed.Where(step => step.Outcome == StepOutcomes.Ok)];
        string[] modelIds = [.. keyed.Select(step => step.ModelId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        var site = new CellSite(runIndex, indexes[0]);
        ReadingFlags flags = ok.Aggregate(ReadingFlags.None, static (union, step) => union | step.Flags) | (run.Document.Baseline?.Flags ?? ReadingFlags.None);

        return new ReportRow(
            run.Document.Run.Tier,
            run.Variant,
            task,
            agent,
            Array.AsReadOnly(modelIds),
            keyed.Length,
            keyed.Count(step => step.Outcome == StepOutcomes.Cancelled),
            keyed.Count(step => step.Outcome == StepOutcomes.Error),
            keyed.Count(step => step.Outcome == StepOutcomes.Incomplete),
            BuildCell("grossJoules", site, ok, static step => step.GrossJoules),
            BuildCell("netJoules", site, ok, static step => step.NetJoules),
            BuildCell("avgW", site, ok, static step => step.AvgW),
            BuildCell("peakW", site, ok, static step => step.PeakW),
            BuildCell("inputTokens", site, ok, static step => step.InputTokens),
            BuildCell("outputTokens", site, ok, static step => step.OutputTokens),
            BuildCell("latencyMs", site, ok, static step => step.LatencyMs),
            BuildCell("ttftMs", site, ok, static step => step.TtftMs),
            BuildCell("cost", site, ok, static step => step.Cost),
            BuildCell("quality", site, ok, static step => step.Quality),
            flags);
    }

    // Where a row starts in the input: the index of its run in the list given to Build and of its first step in Document.Steps. The messages of a refused cell name these two and the member, nothing of the file.
    private readonly record struct CellSite(int Run, int Step);

    private static ReportCell BuildCell(string member, CellSite site, StepReading[] ok, Func<StepReading, Metric> select)
    {
        if (ok.Length == 0)
        {
            return Missing(0, MetricLabel.NotApplicable, [NoOkStepReason]);
        }

        Metric[] metrics = [.. ok.Select(select)];
        Metric[] valued = [.. metrics.Where(metric => metric.Value is not null)];
        if (valued.Length == 0)
        {
            MetricLabel label = metrics.Any(metric => metric.Label == MetricLabel.NotApplicable) ? MetricLabel.NotApplicable : MetricLabel.NotReported;
            string[] reasons = [.. metrics.Select(metric => metric.Reason).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

            return Missing(ok.Length, label, reasons);
        }

        // A cost is never converted: repeats in more than one currency have no common value.
        if (valued.Select(metric => metric.Currency).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            return Missing(ok.Length, MetricLabel.NotApplicable, [MixedCurrencyReason]);
        }

        // A validated file cannot hold two labels or two units in one cell (the per-member shape rules); a document built another way is refused, not averaged.
        if (valued.Select(metric => metric.Label).Distinct().Count() > 1)
        {
            throw new ArgumentException($"runs[{site.Run}].Document.Steps[{site.Step}].{member}: the valued repeats of its row hold more than one label.", RunsParameter);
        }

        if (valued.Select(metric => metric.Unit).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            throw new ArgumentException($"runs[{site.Run}].Document.Steps[{site.Step}].{member}: the valued repeats of its row hold more than one unit.", RunsParameter);
        }

        double[] sorted = [.. valued.Select(metric => metric.Value!.Value).Order()];

        return new ReportCell(
            MedianOf(sorted),
            sorted[0],
            sorted[^1],
            sorted.Length,
            ok.Length,
            valued[0].Label,
            [],
            valued[0].Unit,
            valued[0].Currency);
    }

    private static ReportCell Missing(int total, MetricLabel label, string[] reasons) =>
        new(null, null, null, 0, total, label, Array.AsReadOnly(reasons), null, null);

    // The middle value of an odd count; the mean of the two middle values of an even count, halved first when the sum would overflow.
    private static double MedianOf(double[] sorted)
    {
        int middle = sorted.Length / 2;
        if (sorted.Length % 2 == 1)
        {
            return sorted[middle];
        }

        double mean = (sorted[middle - 1] + sorted[middle]) / 2.0;

        return double.IsFinite(mean) ? mean : (sorted[middle - 1] / 2.0) + (sorted[middle] / 2.0);
    }

    private static ReadOnlyCollection<string> BuildDisclosures(List<TierReport> tiers)
    {
        var disclosures = new List<string> { ReportSentences.GpuBoardOnly, ReportSentences.JudgeExcluded };
        if (tiers.Any(report => report.SelfJudged == true))
        {
            disclosures.Add(ReportSentences.SelfJudged);
        }

        if (tiers.Any(report => report.Mode == RunMode.Concurrent))
        {
            disclosures.Add(ReportSentences.ConcurrentOverlap);
        }

        if (ModelsDifferLine(tiers) is { } differ)
        {
            disclosures.Add(differ);
        }

        return disclosures.AsReadOnly();
    }

    // A fact, never a comparison: what each primary tier ran, listed in tier order, only when the tiers did not all run the same models at the same precision.
    private static string? ModelsDifferLine(List<TierReport> tiers)
    {
        (string Tier, string Text)[] primary =
        [
            .. tiers
                .Where(report => report.Variant == RunVariant.Primary)
                .Select(report => (TierOptions.NameOf(report.Tier), ModelsText(report))),
        ];

        return primary.Length < 2 || primary.Select(entry => entry.Text).Distinct(StringComparer.Ordinal).Count() < 2
            ? null
            : ReportSentences.ModelsDifferPrefix + string.Join(", ", primary.Select(entry => $"{entry.Tier}={entry.Text}"));
    }

    private static string ModelsText(TierReport report)
    {
        string[] ids = [.. report.Models.Select(model => model.ModelId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        return $"{(ids.Length == 0 ? NotAvailable : string.Join('+', ids))} ({report.Precision ?? NotAvailable})";
    }
}