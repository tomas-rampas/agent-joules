using System.Globalization;
using System.Text;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Render;
using AgentJoules.Reporting.Results;

using LabelMetric = AgentJoules.Core.Labels.Metric;

namespace AgentJoules.Reporting.Readme;

/// <summary>
/// The results block of the README as a pure function of the report model (AJ-087, REQ-016, EDGE-002, design 3.9): the heading, the header lines every report surface prints (<see cref="ReportHeader"/>: where and
/// when each file ran, on what, with which models and judge, and the disclosure sentences), and one table of eleven columns. The table has one row per run file in tier order (a baseline file right after its tier,
/// as <c>&lt;tier&gt; baseline</c>) and the row <c>&lt;tier&gt; | not run</c> for each tier without a primary file. A row shows the totals of its file as the file states them, through
/// <see cref="InvariantFormat.Metric"/>: energy and tokens as <c>N/A (reason)</c> or a number, never 0 for a missing value; the cost as one <c>&lt;value&gt; &lt;currency&gt; estimated</c> per currency, never converted or
/// summed across currencies (an entry without a value is <c>N/A &lt;currency&gt; (&lt;reason&gt;)</c>), or <c>N/A (no priced step)</c> for an empty map; the judge totals are not printed. The flags column carries the run flags, <c>partial</c> for a partial run, <c>sampling</c> when a step was
/// metered by sampling and <c>concurrent</c> for a concurrent run; under the table a concurrent run adds <see cref="ReportSentences.ConcurrentCostOverlap"/>. Tiers are never merged, averaged, ranked or
/// compared. The text is deterministic: no clock, file, environment or configuration is read, the invariant culture is used, newlines are LF, and the text is ASCII (every text from a file is cleaned, escaped
/// for Markdown with <see cref="MarkdownText"/>, defused against the GitHub filters that turn a bare address, a <c>www.</c> host, an e-mail address or a <c>:name:</c> shortcode into a link or an image and a
/// dollar sign into math, and then every character above U+007E is written as a numeric character reference, which GitHub renders). The function writes no marker; the update and verify
/// options of the README belong to a later milestone.
/// </summary>
public static class ReadmeResultsBlock
{
    private const string Heading = "### Results";
    private const string DashboardLine = "![Live dashboard during the demo rehearsal](docs/report/dashboard.png)";
    private const string NotAvailable = "N/A";
    private const string NoCost = "N/A (no priced step)";

    // A word joiner (U+2060) as a numeric reference: invisible, and it keeps the filters of GitHub from reading a link, an address or a shortcode out of a text from a file.
    private const string Joiner = "&#8288;";
    private const string DateFormat = "yyyy-MM-dd";
    private const string SamplingFlag = "sampling";
    private const string ConcurrentFlag = "concurrent";
    private const string CostSeparator = "; ";

    private static readonly string[] Headers =
    [
        "tier", "GPU", "run date", "status", "net J (run total)", "gross J (run total)", "input tokens", "output tokens", "cost (run total)", "labels", "flags",
    ];

    /// <summary>Renders the block.</summary>
    /// <param name="model">The report model over the latest run file of each tier and the latest baseline file of each tier.</param>
    /// <param name="dashboardScreenshotExists">
    /// True when <c>docs/report/dashboard.png</c> exists where the block is generated: the block then ends with the image line <c>![Live dashboard during the demo rehearsal](docs/report/dashboard.png)</c>.
    /// The caller decides; the function does not look at the file system.
    /// </param>
    /// <returns>The block: ASCII, LF newlines, no carriage return, ending with one line feed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is null.</exception>
    public static string Render(ReportModel model, bool dashboardScreenshotExists)
    {
        ArgumentNullException.ThrowIfNull(model);

        var text = new StringBuilder();
        text.Append(Heading).Append('\n');
        foreach (string line in ReportHeader.Lines(model, Name))
        {
            text.Append(line).Append('\n');
        }

        text.Append('\n').Append(Line(Headers)).Append('\n').Append('|');
        for (int column = 0; column < Headers.Length; column++)
        {
            text.Append("---|");
        }

        text.Append('\n');
        foreach (string row in Rows(model))
        {
            text.Append(row).Append('\n');
        }

        if (model.Tiers.Any(report => report.Mode == RunMode.Concurrent))
        {
            text.Append('\n').Append(ReportSentences.ConcurrentCostOverlap).Append('\n');
        }

        if (dashboardScreenshotExists)
        {
            text.Append('\n').Append(DashboardLine).Append('\n');
        }

        return text.ToString();
    }

    // A text from a file: cleaned and escaped for Markdown, defused, then written in ASCII.
    private static string Name(string text) => Ascii(Defuse(MarkdownText.Escape(text)));

    private static string Reason(string text) => Ascii(Defuse(MarkdownText.Escape(text, ReportText.ReasonMaxLength)));

    // GitHub links a bare address, a www host and an e-mail address and turns :name: into an image after the Markdown is rendered, whatever the escape (measured by the reviewer): a word joiner before each
    // colon and dot breaks every one of them (the dot alone ends an e-mail address, and a joiner before an at sign would turn a word into a user mention) and leaves the text unchanged on screen. A dollar sign is replaced the way ReportText.Clean replaces an unsafe character, because GitHub renders
    // a dollar pair as math even when it is escaped; the Markdown escape put a backslash before every dollar sign, and that backslash goes with it. The numeric references this adds are not escaped again.
    private static string Defuse(string escaped)
    {
        if (escaped.AsSpan().IndexOfAny("$:.") < 0)
        {
            return escaped;
        }

        var text = new StringBuilder(escaped.Length + 24);
        for (int index = 0; index < escaped.Length; index++)
        {
            char c = escaped[index];
            if (c == '\\' && index + 1 < escaped.Length && escaped[index + 1] == '$')
            {
                text.Append('?');
                index++;
            }
            else if (c == '$')
            {
                text.Append('?');
            }
            else
            {
                _ = c is ':' or '.' ? text.Append(Joiner).Append(c) : text.Append(c);
            }
        }

        return text.ToString();
    }

    // Every character above U+007E becomes a numeric character reference after the Markdown escape, so the references themselves are not escaped; a surrogate pair is one reference to its code point.
    private static string Ascii(string text)
    {
        if (!text.AsSpan().ContainsAnyExceptInRange('\0', '~'))
        {
            return text;
        }

        var ascii = new StringBuilder(text.Length + 16);
        foreach (Rune rune in text.EnumerateRunes())
        {
            _ = rune.Value <= '~' ? ascii.Append((char)rune.Value) : ascii.Append("&#").Append(rune.Value.ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        return ascii.ToString();
    }

    // The rows in the order of the report: tiers in tier order, the primary row (or the not-run row) of a tier before its baseline row. The sort is stable, so the not-run rows, added first, come before the
    // rows of the same place.
    private static IEnumerable<string> Rows(ReportModel model)
    {
        var entries = new List<(int Rank, string Line)>();
        foreach (Tier tier in ReportHeader.NotRun(model))
        {
            entries.Add((ReportHeader.Rank(tier, RunVariant.Primary), Line([TierOptions.NameOf(tier), ReportSentences.NotRun, .. Enumerable.Repeat(string.Empty, Headers.Length - 2)])));
        }

        foreach (TierReport report in model.Tiers)
        {
            entries.Add((ReportHeader.Rank(report.Tier, report.Variant), Line(FileRow(report))));
        }

        return entries.OrderBy(entry => entry.Rank).Select(entry => entry.Line);
    }

    // One run file: the cells come from its totals as written, never from the rows and never from another file.
    private static string[] FileRow(TierReport report)
    {
        Totals totals = report.Totals;
        KeyValuePair<string, LabelMetric>[] cost = [.. totals.CostByCurrency.OrderBy(entry => entry.Key, StringComparer.Ordinal)];
        IEnumerable<MetricLabel> labels = new[] { totals.NetJoules, totals.GrossJoules, totals.InputTokens, totals.OutputTokens }
            .Concat(cost.Select(entry => entry.Value))
            .Select(metric => metric.Label)
            .Concat(cost.Length == 0 ? [MetricLabel.NotApplicable] : []);

        return
        [
            ReportTable.TierName(report.Tier, report.Variant),
            report.GpuName is null ? NotAvailable : Fact(report.GpuName),
            report.StartUtc.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture),
            ReportHeader.Status(report.Status),
            InvariantFormat.Metric(totals.NetJoules, CellKind.Joules, Reason),
            InvariantFormat.Metric(totals.GrossJoules, CellKind.Joules, Reason),
            InvariantFormat.Metric(totals.InputTokens, CellKind.Count, Reason),
            InvariantFormat.Metric(totals.OutputTokens, CellKind.Count, Reason),
            cost.Length == 0 ? NoCost : string.Join(CostSeparator, cost.Select(CostEntry)),
            ReportTable.Labels(labels),
            string.Join(", ", Flags(report)),
        ];
    }

    // One currency of the cost: <value> <CUR> estimated, or, for an entry without a value, the label, the currency key of the entry and the reason (N/A USD (reason)), so two unvalued currencies read differently.
    private static string CostEntry(KeyValuePair<string, LabelMetric> entry)
    {
        string text = InvariantFormat.Metric(entry.Value, CellKind.Cost, Reason);
        if (entry.Value.Value is not null)
        {
            return text;
        }

        string label = MetricLabelText.Of(entry.Value.Label);
        string key = Reason(entry.Key);

        return $"{label} {(key.Length == 0 ? NotAvailable : key)}{text[label.Length..]}";
    }

    // A fact that is empty after the cleaning is N/A, so a cell never has a field with no text.
    private static string Fact(string text)
    {
        string escaped = Name(text);

        return escaped.Length == 0 ? NotAvailable : escaped;
    }

    // What qualifies the numbers of the file: its run flags and the word partial (from the shared helper), then sampling when a step was metered by sampling, then concurrent for a concurrent run.
    private static IEnumerable<string> Flags(TierReport report)
    {
        foreach (string flag in ReportTable.RunFlags(report, Name))
        {
            yield return flag;
        }

        if (report.EnergyMethods.Contains(EnergyMethod.Sampling))
        {
            yield return SamplingFlag;
        }

        if (report.Mode == RunMode.Concurrent)
        {
            yield return ConcurrentFlag;
        }
    }

    // A table line: a cell that has text is " text |", an empty cell is " |", so the not-run row is "| <tier> | not run |" followed by nine " |".
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