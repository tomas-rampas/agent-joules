using System.Text.RegularExpressions;

using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Render;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-083 scenario (the Markdown summary), handed to the step class by Reqnroll context injection. The Given steps build run documents in memory with <see cref="RunDocumentFixtures"/> and
/// <see cref="ReportRunFixtures"/> (the committed fixture files are never read or changed); the When step runs the real <see cref="ReportAggregator"/> and <see cref="SummaryMarkdownRenderer"/>; the Then
/// steps read the text it returned, split into header lines and table cells. There is no file, no HTTP, no model and no GPU.
/// </summary>
public sealed partial class SummaryMarkdownContext
{
    [GeneratedRegex(@"(?<!\\)\|", RegexOptions.CultureInvariant)]
    private static partial Regex UnescapedPipe();

    private readonly List<SelectedRun> _runs = [];

    /// <summary>The rendered summary, or null before the When step.</summary>
    public string? Text { get; private set; }

    /// <summary>Adds a local file with one measured step.</summary>
    public void AddLocalRun() => _runs.Add(ReportRunFixtures.RunOf(Tier.Local, ReportRunFixtures.NetSteps(Tier.Local, 10.0)));

    /// <summary>Adds a nvidia file with one hosted step: the energy is N/A and the tokens and cost have values.</summary>
    public void AddNvidiaRun() => _runs.Add(ReportRunFixtures.RunOf(Tier.Nvidia, ReportRunFixtures.NetSteps(Tier.Nvidia, 1.0)));

    /// <summary>Adds the file of a run whose <c>run.mode</c> is <c>concurrent</c>.</summary>
    public void AddConcurrentRun() => _runs.Add(ReportRunFixtures.Wrap(RunDocumentFixtures.Concurrent(Tier.Local)));

    /// <summary>Adds the file of a hosted run whose response had no usage.</summary>
    public void AddNoUsageRun() => _runs.Add(ReportRunFixtures.Wrap(RunDocumentFixtures.HostedNoUsage()));

    /// <summary>Adds the file of a local run whose one step carries the <c>low-resolution</c> flag.</summary>
    public void AddLowResolutionRun() => _runs.Add(ReportRunFixtures.Wrap(RunDocumentFixtures.LowResolution()));

    /// <summary>Adds a local and a nebius file whose model IDs and precision differ.</summary>
    public void AddRunsWithDifferentModels()
    {
        _runs.Add(ReportRunFixtures.RunOf(Tier.Local, ReportRunFixtures.NetSteps(Tier.Local, 10.0), edit: document => WithModel(document, "Q4_K_M", "model-one")));
        _runs.Add(ReportRunFixtures.RunOf(Tier.Nebius, ReportRunFixtures.NetSteps(Tier.Nebius, 1.0), edit: document => WithModel(document, "FP8", "model-two")));
    }

    /// <summary>Runs the real aggregator and the real renderer over the files added so far.</summary>
    public void Render() => Text = SummaryMarkdownRenderer.Render(ReportAggregator.Build(_runs));

    /// <summary>The header bullet of a tier, for example <c>- local: file ...</c>.</summary>
    /// <param name="tier">The tier name.</param>
    /// <returns>The bullet line.</returns>
    public string Bullet(string tier) =>
        Lines().Single(line => line.StartsWith($"- {tier}: ", StringComparison.Ordinal));

    /// <summary>The lines of the rendered text.</summary>
    /// <returns>The lines, without the line feeds.</returns>
    public string[] Lines() => RequireText().Split('\n');

    /// <summary>The table lines (header, separator and rows).</summary>
    /// <returns>The lines that start with a pipe.</returns>
    public string[] TableLines() => [.. Lines().Where(line => line.StartsWith('|'))];

    /// <summary>The one table row that is not a not-run row, split into trimmed cells.</summary>
    /// <returns>The cells of the one row of a run.</returns>
    public string[] ValueRow() => [.. TableLines().Skip(2).Select(CellsOf).Single(cells => cells[ColumnOf("task")] != ReportSentences.NotRun)];

    /// <summary>The index of a column by its header text.</summary>
    /// <param name="header">The header text, for example <c>gross J</c>.</param>
    /// <returns>The zero-based index.</returns>
    public int ColumnOf(string header) => Array.IndexOf(CellsOf(TableLines()[0]), header) is var index and >= 0
        ? index
        : throw new InvalidOperationException($"The table has no column {header}.");

    /// <summary>Splits a table line on its unescaped pipes.</summary>
    /// <param name="line">The table line.</param>
    /// <returns>The trimmed cells.</returns>
    public static string[] CellsOf(string line) => [.. UnescapedPipe().Split(line).Skip(1).SkipLast(1).Select(cell => cell.Trim())];

    private string RequireText() => Text ?? throw new InvalidOperationException("The summary was not rendered.");

    private static RunDocument WithModel(RunDocument document, string precision, string modelId) =>
        document with { Environment = document.Environment with { Precision = precision, Models = [new EnvironmentModel(ReportRunFixtures.Planner, modelId, Baseline: false)] } };
}