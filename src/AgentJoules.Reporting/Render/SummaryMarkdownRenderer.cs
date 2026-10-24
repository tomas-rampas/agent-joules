using System.Text;

using AgentJoules.Core.Configuration;
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

        text.Append('\n').Append(Line(ReportTable.Headers)).Append('\n').Append('|');
        for (int column = 0; column < ReportTable.Columns; column++)
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
            entries.Add((ReportHeader.Rank(tier, RunVariant.Primary), Line([TierOptions.NameOf(tier), ReportSentences.NotRun, .. Enumerable.Repeat(string.Empty, ReportTable.Columns - 2)])));
        }

        // A file with no row (a partial run that stopped before its first step finished) has its bullet, so the table says so too instead of leaving the tier out.
        foreach (TierReport report in model.Tiers.Where(report => !model.Rows.Any(row => row.Tier == report.Tier && row.Variant == report.Variant)))
        {
            entries.Add((ReportHeader.Rank(report.Tier, report.Variant), Line([ReportTable.TierName(report.Tier, report.Variant), ReportSentences.NoCompletedSteps, .. Enumerable.Repeat(string.Empty, ReportTable.Columns - 3), string.Join(", ", ReportTable.RunFlags(report, Name))])));
        }

        foreach (ReportRow row in model.Rows)
        {
            _ = tierReports.TryGetValue((row.Tier, row.Variant), out TierReport? report);
            entries.Add((ReportHeader.Rank(row.Tier, row.Variant), DataRow(row, report)));
        }

        return entries.OrderBy(entry => entry.Rank).Select(entry => entry.Line);
    }

    private static string DataRow(ReportRow row, TierReport? report) =>
        Line(
        [
            ReportTable.TierName(row.Tier, row.Variant),
            Name(row.Task),
            Name(row.Agent),
            .. ReportTable.CellTexts(row, Reason),
            ReportTable.Labels(row),
            ReportTable.Flags(row, report, Name),
        ]);

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