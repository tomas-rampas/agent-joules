using System.Text;
using System.Text.RegularExpressions;

using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Render;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-085 scenario (the static HTML page), handed to the step class by Reqnroll context injection. The Given steps build run documents in memory with <see cref="ReportRunFixtures"/> (the
/// committed fixture files are never read or changed) or prepare a <c>docs</c> folder inside a <see cref="TempWorkspace"/> (the repository is never written); the When steps run the real
/// <see cref="ReportAggregator"/> and <see cref="ReportHtmlRenderer"/>, or the real <see cref="NoJekyllWriter"/>, which stands for the step of <c>AJ report</c> that writes the marker; the Then steps read the
/// text or the file. The test project has no HTML parser, so the Then steps count tags with regular expressions over the one fixed, lowercase form the renderer writes. There is no HTTP, no model and no GPU.
/// </summary>
public sealed partial class HtmlPageContext : IDisposable
{
    private const string DocsFolder = "docs";

    private readonly List<SelectedRun> _runs = [];
    private TempWorkspace? _workspace;

    /// <summary>The number of aggregate rows of the fixture with three tasks, four agents and one tier.</summary>
    public static int ExpectedRows => 12;

    /// <summary>The rendered page, or null before the When step.</summary>
    public string? Html { get; private set; }

    /// <summary>The bytes the Given step wrote into <c>docs/.nojekyll</c>, or null when it wrote none.</summary>
    public byte[]? KnownContent { get; private set; }

    /// <summary>The path of <c>docs/.nojekyll</c> in the workspace; throws before a docs folder was prepared.</summary>
    public string NoJekyllPath => Path.Combine(DocsPath, NoJekyllWriter.FileName);

    private string DocsPath => Path.Combine((_workspace ?? throw new InvalidOperationException("No docs folder was prepared.")).Root, DocsFolder);

    /// <summary>Adds a local file and a nvidia file with one step each.</summary>
    public void AddLocalAndNvidia()
    {
        _runs.Add(ReportRunFixtures.RunOf(Tier.Local, ReportRunFixtures.NetSteps(Tier.Local, 10.0)));
        _runs.Add(ReportRunFixtures.RunOf(Tier.Nvidia, ReportRunFixtures.NetSteps(Tier.Nvidia, 10.0)));
    }

    /// <summary>Adds one local file with three tasks and four agents, one repeat each, so twelve aggregate rows.</summary>
    public void AddThreeTasksAndFourAgents()
    {
        string[] tasks = ["alpha", "beta", "gamma"];
        string[] agents = ["Planner", "Researcher", "Writer", "Reviewer"];
        _runs.Add(
            ReportRunFixtures.RunOf(
                Tier.Local,
                [.. tasks.SelectMany(task => agents.Select(agent => ReportRunFixtures.Step(Tier.Local, task: task, agent: agent)))]));
    }

    /// <summary>Adds a local file whose task, agent and model ID all carry the given text.</summary>
    /// <param name="text">The hostile text.</param>
    public void AddLocalStepNamed(string text) =>
        _runs.Add(
            ReportRunFixtures.RunOf(
                Tier.Local,
                [ReportRunFixtures.Step(Tier.Local, task: text, agent: text, modelId: text)],
                edit: document => document with { Environment = document.Environment with { Models = [new EnvironmentModel(text, text, Baseline: false)] } }));

    /// <summary>Runs the real aggregator and the real renderer over the files added so far, with no repository URL.</summary>
    public void Render() => Html = ReportHtmlRenderer.Render(ReportAggregator.Build(_runs), repositoryUrl: null);

    /// <summary>Creates a workspace with an empty <c>docs</c> folder; when <paramref name="content"/> is given, <c>docs/.nojekyll</c> holds it.</summary>
    /// <param name="content">The known content of the marker, or null for no marker.</param>
    public void PrepareDocsFolder(string? content)
    {
        _workspace = TempWorkspace.CreateEmpty();
        Directory.CreateDirectory(DocsPath);
        if (content is not null)
        {
            KnownContent = Encoding.ASCII.GetBytes(content);
            File.WriteAllBytes(NoJekyllPath, KnownContent);
        }
    }

    /// <summary>Runs the real <see cref="NoJekyllWriter"/> on the docs folder.</summary>
    public void WriteReport() => _ = NoJekyllWriter.EnsureExists(DocsPath);

    /// <summary>The number of <c>table</c> start tags of the page.</summary>
    /// <returns>The count.</returns>
    public int TableCount() => TableTag().Count(Page());

    /// <summary>The number of rows in the first table body (the aggregate rows).</summary>
    /// <returns>The count.</returns>
    public int AggregateRowCount() => RowTag().Count(FirstBody().Match(Page()).Groups[1].Value);

    /// <summary>The values of the <c>src</c> attributes of the page, quotes removed.</summary>
    /// <returns>The values.</returns>
    public string[] SrcValues() => [.. SrcAttribute().Matches(Page()).Select(match => match.Groups[1].Value.Trim('"', '\''))];

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();

    [GeneratedRegex(@"<table[ >]", RegexOptions.IgnoreCase)]
    private static partial Regex TableTag();

    [GeneratedRegex(@"<tbody>(.*?)</tbody>", RegexOptions.Singleline)]
    private static partial Regex FirstBody();

    [GeneratedRegex(@"<tr>")]
    private static partial Regex RowTag();

    [GeneratedRegex(@"\ssrc\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SrcAttribute();

    private string Page() => Html ?? throw new InvalidOperationException("The page was not rendered.");
}