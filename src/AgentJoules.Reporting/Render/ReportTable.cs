using System.Globalization;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;

namespace AgentJoules.Reporting.Render;

/// <summary>
/// The one place the report surfaces take the texts of a table row from (AJ-087, REQ-016): the fifteen column headers of the Markdown summary and the HTML page, the order of the labels in a labels column, the
/// tier cell, the ten value cells of a row through <see cref="InvariantFormat.Cell"/>, and the flags text of a row or of a run file. Every method returns plain strings; the escaping that belongs to a surface
/// (Markdown escaping, or the cleaning that precedes HTML encoding) is passed in or applied by the caller afterwards, so the summary, the page and the README block cannot drift apart in what a cell says.
/// </summary>
internal static class ReportTable
{
    /// <summary>The number of columns of the summary and the page.</summary>
    internal const int Columns = 15;

    private const string Partial = "partial";

    // The order of the labels in a labels column: the vocabulary of the results schema.
    private static readonly MetricLabel[] LabelOrder =
    [
        MetricLabel.Measured, MetricLabel.Estimated, MetricLabel.ModelJudged, MetricLabel.NotApplicable, MetricLabel.NotReported,
    ];

    private static readonly CellKind[] Kinds =
    [
        CellKind.Joules, CellKind.Joules, CellKind.Watts, CellKind.Watts, CellKind.Count, CellKind.Count, CellKind.Milliseconds, CellKind.Milliseconds, CellKind.Cost, CellKind.Quality,
    ];

    /// <summary>The headers of the fifteen columns of the summary and the page.</summary>
    internal static IReadOnlyList<string> Headers { get; } =
    [
        "tier", "task", "agent", "gross J", "net J", "avg W", "peak W", "input tokens", "output tokens", "latency ms", "TTFT ms", "cost", "quality", "labels", "flags",
    ];

    /// <summary>The tier cell of a run file: the tier name, followed by <c>baseline</c> for the baseline file of the tier.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="variant">Whether the file is the primary or the baseline file.</param>
    /// <returns>The text, for example <c>local</c> or <c>nebius baseline</c>.</returns>
    internal static string TierName(Tier tier, RunVariant variant) => TierOptions.NameOf(tier) + (variant == RunVariant.Baseline ? " baseline" : string.Empty);

    /// <summary>The ten value cells of a row in column order: gross J, net J, avg W, peak W, input tokens, output tokens, latency ms, TTFT ms, cost, quality.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The cells.</returns>
    internal static ReportCell[] Cells(ReportRow row) =>
        [row.GrossJoules, row.NetJoules, row.AvgW, row.PeakW, row.InputTokens, row.OutputTokens, row.LatencyMs, row.TtftMs, row.Cost, row.Quality];

    /// <summary>The texts of the ten value cells of a row, in the cell grammar of <see cref="InvariantFormat.Cell"/>.</summary>
    /// <param name="row">The row.</param>
    /// <param name="escape">The escaping of the surface for a reason or a currency code that comes from a document.</param>
    /// <returns>The ten texts.</returns>
    internal static string[] CellTexts(ReportRow row, Func<string, string> escape)
    {
        ReportCell[] cells = Cells(row);

        return [.. cells.Select((cell, index) => InvariantFormat.Cell(cell, Kinds[index], escape))];
    }

    /// <summary>The labels column of a row: the distinct labels of its value cells.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The labels in the order of the vocabulary, joined by a comma and a space.</returns>
    internal static string Labels(ReportRow row) => Labels(Cells(row).Select(cell => cell.Label));

    /// <summary>The distinct labels, in the order of the vocabulary of the results schema.</summary>
    /// <param name="labels">The labels, in any order and with repeats.</param>
    /// <returns>The label texts joined by a comma and a space; empty for no label.</returns>
    internal static string Labels(IEnumerable<MetricLabel> labels)
    {
        HashSet<MetricLabel> present = [.. labels];

        return string.Join(", ", LabelOrder.Where(present.Contains).Select(MetricLabelText.Of));
    }

    /// <summary>
    /// The flags of a row, as plain strings: the flags of the <c>ok</c> repeats and of the baseline, then the flags of the run file, then the word <c>partial</c> for a partial run, then the repeats that did not
    /// finish (<c>1 cancelled</c>, <c>1 error</c>, <c>2 incomplete</c>): everything that qualifies the numbers of the row, in its row.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="report">The report of the file the row comes from, or null when the model has none.</param>
    /// <param name="runFlag">The cleaning or escaping of the surface for a run flag.</param>
    /// <returns>The flags joined by a comma and a space; empty for none.</returns>
    internal static string Flags(ReportRow row, TierReport? report, Func<string, string> runFlag)
    {
        List<string> flags = [.. ReadingFlagNames.ToStrings(row.Flags)];
        if (report is not null)
        {
            flags.AddRange(RunFlags(report, runFlag));
        }

        AddCount(flags, row.Cancelled, "cancelled");
        AddCount(flags, row.Errors, "error");
        AddCount(flags, row.Incomplete, "incomplete");

        return string.Join(", ", flags);
    }

    /// <summary>What qualifies every row of a file: its run flags, then the word <c>partial</c> when the run stopped early.</summary>
    /// <param name="report">The report of the file.</param>
    /// <param name="runFlag">The cleaning or escaping of the surface for a run flag.</param>
    /// <returns>The flags.</returns>
    internal static IEnumerable<string> RunFlags(TierReport report, Func<string, string> runFlag)
    {
        foreach (string flag in report.RunFlags)
        {
            yield return runFlag(flag);
        }

        if (report.Status == RunStatus.Partial)
        {
            yield return Partial;
        }
    }

    private static void AddCount(List<string> flags, int count, string outcome)
    {
        if (count > 0)
        {
            flags.Add($"{count.ToString(CultureInfo.InvariantCulture)} {outcome}");
        }
    }
}