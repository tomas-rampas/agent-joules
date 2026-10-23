using System.Globalization;
using System.Text;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;

namespace AgentJoules.Reporting.Render;

/// <summary>
/// Renders the report model as <c>summary.md</c> (AJ-083, REQ-016, EDGE-002, EDGE-003, EDGE-004, EDGE-007): the title, the header of <see cref="ReportHeader"/>, and one table of fifteen columns whose cells keep their
/// label and reason. A value that does not exist prints its label and reason (<c>N/A (hosted-tier)</c>, <c>not-reported (provider-omitted-usage)</c>), never a number and never 0; a label other than measured is
/// printed in its cell; the flags of a row are printed in its flags column; a tier without a run file is the row <c>&lt;tier&gt; | not run</c> and a run file without a row is the row <c>&lt;tier&gt; | no completed steps</c>. Nothing is ranked, compared, summed or averaged across tiers or
/// variants, and the energy scope sentence comes from <see cref="ReportSentences"/>. The text is deterministic: invariant culture, LF newlines, ASCII for everything the renderer writes, and no clock is read. Every
/// text that comes from a document is escaped with <see cref="MarkdownText"/>.
/// </summary>
public static class SummaryMarkdownRenderer
{
    private const string Title = "# Agent Joules results";
    private const string Partial = "partial";
    private const int Columns = 15;

    private static readonly string[] Headers =
    [
        "tier", "task", "agent", "gross J", "net J", "avg W", "peak W", "input tokens", "output tokens", "latency ms", "TTFT ms", "cost", "quality", "labels", "flags",
    ];

    // The order of the labels in the labels column: the vocabulary of the results schema.
    private static readonly MetricLabel[] LabelOrder =
    [
        MetricLabel.Measured, MetricLabel.Estimated, MetricLabel.ModelJudged, MetricLabel.NotApplicable, MetricLabel.NotReported,
    ];

    /// <summary>Renders the summary.</summary>
    /// <param name="model">The report model.</param>
    /// <returns>The Markdown text; it ends with one line feed and holds no carriage return.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is null.</exception>
    public static string Render(ReportModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var text = new StringBuilder();
        text.Append(Title).Append('\n');
        foreach (string line in ReportHeader.Lines(model, Name))
        {
            text.Append(line).Append('\n');
        }

        text.Append('\n').Append(Line(Headers)).Append('\n').Append('|');
        for (int column = 0; column < Columns; column++)
        {
            text.Append("---|");
        }

        text.Append('\n');
        foreach (string row in Rows(model))
        {
            text.Append(row).Append('\n');
        }

        return text.ToString();
    }

    private static string Name(string text) => MarkdownText.Escape(text);

    private static string TierName(Tier tier, RunVariant variant) => TierOptions.NameOf(tier) + (variant == RunVariant.Baseline ? " baseline" : string.Empty);

    private static string Reason(string text) => MarkdownText.Escape(text, ReportText.ReasonMaxLength);

    // The rows in the order of the report: tiers in tier order, a tier's primary rows (or its not-run row) before its baseline rows. The sort is stable, so the model keeps its own order inside a place, and
    // the not-run rows, added first, come before the rows of the same place.
    private static IEnumerable<string> Rows(ReportModel model)
    {
        var tierReports = new Dictionary<(Tier, RunVariant), TierReport>();
        foreach (TierReport report in model.Tiers)
        {
            _ = tierReports.TryAdd((report.Tier, report.Variant), report);
        }

        var entries = new List<(int Rank, string Line)>();
        foreach (Tier tier in ReportHeader.NotRun(model))
        {
            entries.Add((ReportHeader.Rank(tier, RunVariant.Primary), Line([TierOptions.NameOf(tier), ReportSentences.NotRun, .. Enumerable.Repeat(string.Empty, Columns - 2)])));
        }

        // A file with no row (a partial run that stopped before its first step finished) has its bullet, so the table says so too instead of leaving the tier out.
        foreach (TierReport report in model.Tiers.Where(report => !model.Rows.Any(row => row.Tier == report.Tier && row.Variant == report.Variant)))
        {
            entries.Add((ReportHeader.Rank(report.Tier, report.Variant), Line([TierName(report.Tier, report.Variant), ReportSentences.NoCompletedSteps, .. Enumerable.Repeat(string.Empty, Columns - 3), string.Join(", ", RunFlagsOf(report))])));
        }

        foreach (ReportRow row in model.Rows)
        {
            _ = tierReports.TryGetValue((row.Tier, row.Variant), out TierReport? report);
            entries.Add((ReportHeader.Rank(row.Tier, row.Variant), DataRow(row, report)));
        }

        return entries.OrderBy(entry => entry.Rank).Select(entry => entry.Line);
    }

    private static string DataRow(ReportRow row, TierReport? report)
    {
        ReportCell[] cells = [row.GrossJoules, row.NetJoules, row.AvgW, row.PeakW, row.InputTokens, row.OutputTokens, row.LatencyMs, row.TtftMs, row.Cost, row.Quality];
        CellKind[] kinds = [CellKind.Joules, CellKind.Joules, CellKind.Watts, CellKind.Watts, CellKind.Count, CellKind.Count, CellKind.Milliseconds, CellKind.Milliseconds, CellKind.Cost, CellKind.Quality];
        string tier = TierName(row.Tier, row.Variant);

        return Line(
        [
            tier,
            Name(row.Task),
            Name(row.Agent),
            .. cells.Select((cell, index) => InvariantFormat.Cell(cell, kinds[index], Reason)),
            LabelsOf(cells),
            FlagsOf(row, report),
        ]);
    }

    private static string LabelsOf(ReportCell[] cells)
    {
        HashSet<MetricLabel> present = [.. cells.Select(cell => cell.Label)];

        return string.Join(", ", LabelOrder.Where(present.Contains).Select(MetricLabelText.Of));
    }

    // The flags of the ok repeats and of the baseline, then the flags of the run, then the word partial, then the repeats that did not finish: everything that qualifies the numbers of the row, in its row.
    private static string FlagsOf(ReportRow row, TierReport? report)
    {
        List<string> flags = [.. ReadingFlagNames.ToStrings(row.Flags)];
        if (report is not null)
        {
            flags.AddRange(RunFlagsOf(report));
        }

        AddCount(flags, row.Cancelled, "cancelled");
        AddCount(flags, row.Errors, "error");
        AddCount(flags, row.Incomplete, "incomplete");

        return string.Join(", ", flags);
    }

    // What qualifies every row of a file: its run flags, then the word partial when the run stopped early. A file without rows shows them in its one row.
    private static IEnumerable<string> RunFlagsOf(TierReport report)
    {
        foreach (string flag in report.RunFlags)
        {
            yield return Name(flag);
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

    // A table line: a cell that has text is " text |", an empty cell is " |", so the not-run row is "| <tier> | not run |" followed by thirteen " |".
    private static string Line(IEnumerable<string> cells)
    {
        var line = new StringBuilder("|");
        foreach (string cell in cells)
        {
            line.Append(cell.Length == 0 ? " |" : $" {cell} |");
        }

        return line.ToString();
    }
}