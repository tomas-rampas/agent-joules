using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Reporting.Results;

namespace AgentJoules.Reporting.Render;

/// <summary>
/// Renders the report model as the static page <c>index.html</c> of the demo URL (AJ-085, REQ-030): the title, the header of <see cref="ReportHeader"/> as a list, the disclosure sentences as paragraphs, one table of
/// fifteen columns whose cells are those of the Markdown summary, the chart <c>joules-per-step.svg</c> as an image of the sibling file, and a link to the README of the repository. The page is one fixed, lowercase
/// form: no script, no link element, no resource other than the sibling SVG, one inline style, LF newlines, and a content security policy that allows the sibling image and that one style (by its hash) and nothing else. The page is ASCII by construction: every character above U+007E of a
/// text from a document is written as a numeric character reference. The first table body holds one row per aggregate (and one <c>no completed steps</c> row
/// per run file without rows); a tier without a run file is a row of a second body, <c>&lt;tier&gt; | not run</c>. Every text that comes from a document is cleaned with <see cref="ReportText"/> and then
/// encoded once (<see cref="WebUtility.HtmlEncode(string?)"/>, then the numeric references), so a model ID, a task, an agent or a reason cannot become markup. The cell grammar is that of <see cref="InvariantFormat"/>; nothing is
/// ranked, compared, summed or averaged across tiers, and no clock is read.
/// </summary>
public static class ReportHtmlRenderer
{
    /// <summary>The file name of the chart the page shows; the page and the SVG are written side by side.</summary>
    public const string ChartFileName = "joules-per-step.svg";

    /// <summary>The sentence the page prints in place of the repository link when no repository URL is configured.</summary>
    public const string RepositoryNotConfigured = "Repository link not configured (Report:RepositoryUrl).";

    /// <summary>The text of the link to the README of the repository.</summary>
    public const string RepositoryLinkText = "Repository README";

    /// <summary>The alternative text of the chart image: a constant, never text lifted from the SVG or the model.</summary>
    public const string ChartAlt = "Net joules per step and tier; N/A where no GPU-board energy was measured";

    private const string Title = "Agent Joules results";
    private const string BulletPrefix = "- ";
    private const string NestedPrefix = "  - ";

    // The only style of the page; the page writes StyleContent between its style tags. No rule names a class of the chart (bar, marker, value, title, scope, axis, row, row-label, group-label, notice) and the image has no width rule: it keeps its own size
    // inside a box that scrolls sideways, so the 10 to 12 px labels of the chart stay legible.
    private static readonly string[] Style =
    [
        "body { font-family: system-ui, sans-serif; margin: 1.5rem; color: #222222; background: #ffffff; }",
        "h1 { font-size: 1.5rem; }",
        ".scroll, .chart { overflow-x: auto; }",
        "table { border-collapse: collapse; font-size: 0.85rem; }",
        "th, td { border: 1px solid #bbbbbb; padding: 0.25rem 0.5rem; text-align: left; vertical-align: top; white-space: nowrap; }",
        "th { background: #f0f0f0; }",
        "tbody.not-run td { color: #555555; font-style: italic; }",
        "img { display: block; }",
    ];

    // The exact text between the style tags, and the policy that allows exactly that text: the hash is computed from the same constant the renderer writes, so the two cannot drift. Nothing else may load or run.
    private static readonly string StyleContent = "\n" + string.Join('\n', Style) + "\n";

    private static readonly string ContentSecurityPolicy =
        "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src 'self'; style-src 'sha256-"
        + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(StyleContent)))
        + "'; base-uri 'none'; form-action 'none'\">\n";

    /// <summary>
    /// Renders the page.
    /// </summary>
    /// <param name="model">The report model.</param>
    /// <param name="repositoryUrl">
    /// The URL of the repository (<c>Report:RepositoryUrl</c>), or null when none is configured. A page with a URL has one link, <c>Repository README</c>, to the URL without its fragment followed by <c>#readme</c>;
    /// a page without one prints <see cref="RepositoryNotConfigured"/> and has no link. The link is built from the scheme, the host in its ASCII (punycode) form, the port and the path, so a fragment is dropped;
    /// a URL with a query, or one that holds an <c>nvapi-</c> key, is refused because the link is published as it stands.
    /// </param>
    /// <returns>The page: ASCII, LF newlines, ending with one line feed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="repositoryUrl"/> is not null and is not an absolute <c>https</c> URL without a user name, a password or a query, or holds an <c>nvapi-</c> key.</exception>
    public static string Render(ReportModel model, Uri? repositoryUrl)
    {
        ArgumentNullException.ThrowIfNull(model);
        string? readme = repositoryUrl is null ? null : RepositoryLink.ReadmeHref(repositoryUrl);

        var page = new StringBuilder(16384);
        page.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append(ContentSecurityPolicy)
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(Title).Append("</title>\n<style>").Append(StyleContent).Append("</style>\n</head>\n<body>\n")
            .Append("<h1>").Append(Title).Append("</h1>\n");

        AppendHeader(page, model);
        AppendTable(page, model);
        page.Append("<div class=\"chart\">\n<img src=\"").Append(ChartFileName).Append("\" alt=\"").Append(ChartAlt).Append("\">\n</div>\n");
        page.Append(readme is null
            ? $"<p>{RepositoryNotConfigured}</p>\n"
            : $"<p><a href=\"{Encode(readme)}\">{RepositoryLinkText}</a></p>\n");
        page.Append("</body>\n</html>\n");

        return page.ToString();
    }

    // A text that comes from a document: cleaned (key masked, cut, unsafe characters replaced) and then encoded, once.
    private static string Name(string text) => Encode(ReportText.Clean(text, ReportText.NameMaxLength));

    // The one encoding of the page: HtmlEncode for the markup characters, then a numeric reference for every character above U+007E (HtmlEncode leaves the characters from U+0100 up as they are), so the page is ASCII
    // whatever the documents hold. A surrogate pair is one reference to its code point.
    private static string Encode(string text)
    {
        string encoded = WebUtility.HtmlEncode(text);
        if (!encoded.AsSpan().ContainsAnyExceptInRange('\0', '~'))
        {
            return encoded;
        }

        var ascii = new StringBuilder(encoded.Length + 16);
        foreach (Rune rune in encoded.EnumerateRunes())
        {
            _ = rune.Value <= '~' ? ascii.Append((char)rune.Value) : ascii.Append("&#").Append(rune.Value.ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        return ascii.ToString();
    }

    private static string Reason(string text) => ReportText.Clean(text, ReportText.ReasonMaxLength);

    // The header lines come from ReportHeader, so the page and the summary cannot drift: the bullets carry their facts already encoded by Name, the sampling-fallback sentence is a nested item, and a disclosure
    // is a paragraph. The models-differ line is the one ReportHeader builds per tier; any other disclosure is encoded here whole, because the lines of ReportHeader escape a leading list marker with a backslash
    // for Markdown, which an HTML page would show as a character.
    private static void AppendHeader(StringBuilder page, ReportModel model)
    {
        IReadOnlyList<string> lines = ReportHeader.Lines(model, Name);
        int disclosureLines = model.Disclosures.Count == 0 ? 0 : model.Disclosures.Count + 1;
        int bullets = lines.Count - disclosureLines;
        if (bullets > 0)
        {
            page.Append("<ul>\n");
            for (int index = 0; index < bullets; index++)
            {
                page.Append("<li>").Append(lines[index][BulletPrefix.Length..]);
                if (index + 1 < bullets && lines[index + 1].StartsWith(NestedPrefix, StringComparison.Ordinal))
                {
                    index++;
                    page.Append("<ul><li>").Append(lines[index][NestedPrefix.Length..]).Append("</li></ul>");
                }

                page.Append("</li>\n");
            }

            page.Append("</ul>\n");
        }

        for (int index = 0; index < model.Disclosures.Count; index++)
        {
            string disclosure = model.Disclosures[index];
            string html = disclosure.StartsWith(ReportSentences.ModelsDifferPrefix, StringComparison.Ordinal) ? lines[bullets + 1 + index] : Name(disclosure);
            page.Append("<p>").Append(html).Append("</p>\n");
        }
    }

    private static void AppendTable(StringBuilder page, ReportModel model)
    {
        page.Append("<div class=\"scroll\">\n<table>\n<thead>\n").Append(Row(ReportTable.Headers.Select(header => $"<th>{header}</th>"))).Append("\n</thead>\n<tbody>\n");
        foreach (string row in AggregateRows(model))
        {
            page.Append(row).Append('\n');
        }

        page.Append("</tbody>\n");
        Tier[] notRun = [.. ReportHeader.NotRun(model)];
        if (notRun.Length > 0)
        {
            page.Append("<tbody class=\"not-run\">\n");
            foreach (Tier tier in notRun)
            {
                page.Append(Row([Cell(TierOptions.NameOf(tier)), $"<td colspan=\"{(ReportTable.Columns - 1).ToString(CultureInfo.InvariantCulture)}\">{ReportSentences.NotRun}</td>"])).Append('\n');
            }

            page.Append("</tbody>\n");
        }

        page.Append("</table>\n</div>\n");
    }

    // The rows in the order of the summary: tiers in tier order, a tier's primary rows before its baseline rows. The sort is stable, so the model keeps its own order inside a place, and the
    // no-completed-steps rows, added first, come before the rows of the same place.
    private static IEnumerable<string> AggregateRows(ReportModel model)
    {
        var tierReports = new Dictionary<(Tier, RunVariant), TierReport>();
        foreach (TierReport report in model.Tiers)
        {
            _ = tierReports.TryAdd((report.Tier, report.Variant), report);
        }

        var entries = new List<(int Rank, string Line)>();

        // A file with no row (a partial run that stopped before its first step finished) has its bullet, so the table says so too instead of leaving the tier out.
        foreach (TierReport report in model.Tiers.Where(report => !model.Rows.Any(row => row.Tier == report.Tier && row.Variant == report.Variant)))
        {
            string[] cells =
            [
                Cell(ReportTable.TierName(report.Tier, report.Variant)),
                Cell(ReportSentences.NoCompletedSteps),
                .. Enumerable.Repeat(Cell(string.Empty), ReportTable.Columns - 3),
                Cell(Encode(string.Join(", ", ReportTable.RunFlags(report, CleanFlag)))),
            ];
            entries.Add((ReportHeader.Rank(report.Tier, report.Variant), Row(cells)));
        }

        foreach (ReportRow row in model.Rows)
        {
            _ = tierReports.TryGetValue((row.Tier, row.Variant), out TierReport? report);
            entries.Add((ReportHeader.Rank(row.Tier, row.Variant), DataRow(row, report)));
        }

        return entries.OrderBy(entry => entry.Rank).Select(entry => entry.Line);
    }

    // The cells come from ReportTable as plain text: a cell that holds text from a document is encoded here, once; the tier, the labels and the fixed words need no encoding. The run flags are cleaned, and the
    // whole flags cell is encoded afterwards.
    private static string DataRow(ReportRow row, TierReport? report) =>
        Row(
        [
            Cell(ReportTable.TierName(row.Tier, row.Variant)),
            Cell(Name(row.Task)),
            Cell(Name(row.Agent)),
            .. ReportTable.CellTexts(row, Reason).Select(text => Cell(Encode(text))),
            Cell(ReportTable.Labels(row)),
            Cell(Encode(ReportTable.Flags(row, report, CleanFlag))),
        ]);

    private static string CleanFlag(string flag) => ReportText.Clean(flag, ReportText.NameMaxLength);

    // The html of a cell: its text is already encoded, or is a fixed word.
    private static string Cell(string html) => $"<td>{html}</td>";

    private static string Row(IEnumerable<string> cells) => $"<tr>{string.Concat(cells)}</tr>";
}