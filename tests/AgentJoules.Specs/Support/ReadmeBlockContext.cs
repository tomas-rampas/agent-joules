using System.Text.RegularExpressions;

using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Readme;
using AgentJoules.Reporting.Render;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-087 scenario (the README results block), handed to the step class by Reqnroll context injection. The Given steps build run documents in memory with
/// <see cref="ReportRunFixtures"/> and <see cref="RunDocumentFixtures"/> (the committed fixture files are never read or changed); the When steps run the real <see cref="ReportAggregator"/> and the real
/// <see cref="ReadmeResultsBlock"/>; the Then steps read the text it returned, split into lines and table cells. There is no file, no HTTP, no model and no GPU.
/// </summary>
public sealed partial class ReadmeBlockContext
{
    [GeneratedRegex(@"(?<!\\)\|", RegexOptions.CultureInvariant)]
    private static partial Regex UnescapedPipe();

    private readonly List<SelectedRun> _runs = [];

    /// <summary>The first render, or null before the When step.</summary>
    public string? Text { get; private set; }

    /// <summary>The second render of the same model, or null when the scenario rendered once.</summary>
    public string? SecondText { get; private set; }

    /// <summary>Adds the committed local, nvidia and nebius documents as files; there is no cloudgpu file.</summary>
    public void AddLocalNvidiaNebius()
    {
        foreach (Tier tier in new[] { Tier.Local, Tier.Nvidia, Tier.Nebius })
        {
            _runs.Add(ReportRunFixtures.Wrap(RunDocumentFixtures.Committed(tier)));
        }
    }

    /// <summary>Adds a nvidia file whose judge model is also a workflow model (<c>run.selfJudged</c> is true).</summary>
    public void AddSelfJudgedNvidia() => _runs.Add(ReportRunFixtures.Wrap(RunDocumentFixtures.SelfJudged(Tier.Nvidia)));

    /// <summary>Adds the committed nvidia document as a file: its totals come from the run assembler, so the energy totals are N/A.</summary>
    public void AddNvidia() => _runs.Add(ReportRunFixtures.Wrap(RunDocumentFixtures.Committed(Tier.Nvidia)));

    /// <summary>Adds the cloudgpu file whose status is <c>partial</c> and whose energy method is <c>sampling</c>.</summary>
    public void AddCloudGpuPartialSampling() => _runs.Add(ReportRunFixtures.Wrap(RunDocumentFixtures.CloudGpuPartialSampling()));

    /// <summary>Runs the real aggregator and the real block over the files added so far, without the dashboard line.</summary>
    public void Render() => Text = ReadmeResultsBlock.Render(ReportAggregator.Build(_runs), dashboardScreenshotExists: false);

    /// <summary>Renders the same model twice.</summary>
    public void RenderTwice()
    {
        ReportModel model = ReportAggregator.Build(_runs);
        Text = ReadmeResultsBlock.Render(model, dashboardScreenshotExists: false);
        SecondText = ReadmeResultsBlock.Render(model, dashboardScreenshotExists: false);
    }

    /// <summary>The lines of the first render.</summary>
    /// <returns>The lines, without the line feeds.</returns>
    public string[] Lines() => RequireText().Split('\n');

    /// <summary>The table lines (header, separator and rows).</summary>
    /// <returns>The lines that start with a pipe.</returns>
    public string[] TableLines() => [.. Lines().Where(line => line.StartsWith('|'))];

    /// <summary>The row of a tier, split into trimmed cells.</summary>
    /// <param name="tier">The text of the tier cell, for example <c>nvidia</c>.</param>
    /// <returns>The cells.</returns>
    public string[] RowOf(string tier) => [.. TableLines().Skip(2).Select(CellsOf).Single(cells => cells[0] == tier)];

    /// <summary>The index of a column by its header text.</summary>
    /// <param name="header">The header text, for example <c>net J (run total)</c>.</param>
    /// <returns>The zero-based index.</returns>
    public int ColumnOf(string header) => Array.IndexOf(CellsOf(TableLines()[0]), header) is var index and >= 0
        ? index
        : throw new InvalidOperationException($"The table has no column {header}.");

    /// <summary>Splits a table line on its unescaped pipes.</summary>
    /// <param name="line">The table line.</param>
    /// <returns>The trimmed cells.</returns>
    public static string[] CellsOf(string line) => [.. UnescapedPipe().Split(line).Skip(1).SkipLast(1).Select(cell => cell.Trim())];

    private string RequireText() => Text ?? throw new InvalidOperationException("The block was not rendered.");
}