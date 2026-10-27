using System.Globalization;
using System.Text.RegularExpressions;

using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>AJ-152: the generated outputs against the committed results (determinism, the page, the posts, the shot list).</summary>
public sealed partial class EvidenceSteps
{
    private const string ReportCommand = "report";

    private static readonly string[] GenerateTokens = [ReportCommand, "--in", "results/", "--out", "docs/report/", "--update-readme", "--render-content"];

    // A fixture folder for the report command: the committed results, the README (with the result markers) and the content templates.
    private TempWorkspace ReportFixture()
    {
        TempWorkspace workspace = _context.NewFixture();
        foreach (string name in EvidenceChecks.ResultFileNames(RepositoryRoot.Path))
        {
            EvidenceContext.CopyCommitted(workspace, "results/" + name);
        }

        // The report reads Report:RepositoryUrl from the configuration file; the committed page carries that link.
        EvidenceContext.CopyCommitted(workspace, "config/appsettings.json");
        _ = workspace.WriteFile("README.md", ReadmeFixtures.WithMarkers(RepositoryRoot.ReadAllText("README.md")));
        // The rendered posts are left out, so the currency check proves that the run wrote them; the renderer reads the template.
        foreach (string path in Directory.EnumerateFiles(RepositoryRoot.Resolve("content")).Where(static path => !string.Equals(Path.GetFileName(path), "linkedin-posts.md", StringComparison.Ordinal)))
        {
            EvidenceContext.CopyCommitted(workspace, "content/" + Path.GetFileName(path));
        }

        return workspace;
    }

    // The README, every file of docs/report/ and the rendered posts of the fixture, compared byte for byte with the committed ones.
    private static string[] CommittedOutputViolations(string root)
    {
        string[] relative =
        [
            "README.md",
            "content/linkedin-posts.md",
            .. Directory.EnumerateFiles(Path.Combine(root, "docs", "report"), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')),
            .. Directory.EnumerateFiles(RepositoryRoot.Resolve("docs/report"), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(RepositoryRoot.Path, path).Replace('\\', '/')),
        ];
        return
        [
            .. relative.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Where(path =>
            {
                string generated = Path.Combine(root, path);
                string committed = RepositoryRoot.Resolve(path);
                return !File.Exists(generated) || !File.Exists(committed) || !File.ReadAllBytes(generated).AsSpan().SequenceEqual(File.ReadAllBytes(committed));
            }),
        ];
    }

    private TempWorkspace PageFixture(bool noJekyll, string extra)
    {
        TempWorkspace workspace = _context.NewFixture();
        _ = workspace.WriteFile("docs/report/index.html", RepositoryRoot.ReadAllText("docs/report/index.html") + extra);
        if (noJekyll)
        {
            _ = workspace.WriteFile("docs/.nojekyll", string.Empty);
        }

        _context.Check = () => EvidenceChecks.PageViolations(_context.Root);
        return workspace;
    }

    [Given(@"^a workspace with the committed results and a README with markers$")]
    public void GivenAWorkspaceWithResultsAndReadme() => _ = ReportFixture();

    [When(@"^""report --in results/ --out docs/report/ --update-readme --render-content"" runs twice$")]
    public async Task WhenTheReportRunsTwice()
    {
        await _context.RunCommandAsync(GenerateTokens);
        Assert.True(_context.Command!.ExitCode == 0, $"first run: exit {_context.Command.ExitCode}; stdout: {_context.Command.Stdout}; stderr: {_context.Command.Stderr}");
        _context.FirstHashes = _context.HashTree();
        _context.CurrentnessViolations = CommittedOutputViolations(_context.Root);

        await _context.RunCommandAsync(GenerateTokens);
        Assert.True(_context.Command!.ExitCode == 0, $"second run: exit {_context.Command.ExitCode}; stdout: {_context.Command.Stdout}; stderr: {_context.Command.Stderr}");
        _context.SecondHashes = _context.HashTree();
    }

    [Then(@"^the second run changes no file and ""report --verify-readme"" exits 0$")]
    public async Task ThenTheSecondRunChangesNothing()
    {
        string[] changed = [.. _context.FirstHashes.Keys.Union(_context.SecondHashes.Keys).Where(path => !_context.FirstHashes.TryGetValue(path, out string? first) || !_context.SecondHashes.TryGetValue(path, out string? second) || first != second).Order(StringComparer.Ordinal)];
        Assert.True(changed.Length == 0, "files that differ between the two runs: " + string.Join(", ", changed));
        Assert.True(_context.SecondHashes.ContainsKey("docs/report/index.html"), "the report page was not written");

        // The generated outputs are also the committed ones (AJ-152 criteria 1 and 5: regenerating leaves `git status` clean).
        Assert.True(_context.CurrentnessViolations.Count == 0, "generated files that differ from the committed ones: " + string.Join(", ", _context.CurrentnessViolations));

        await _context.RunCommandAsync(ReportCommand, "--verify-readme");
        Assert.True(_context.Command!.ExitCode == 0, $"verify-readme: exit {_context.Command.ExitCode}; stdout: {_context.Command.Stdout}; stderr: {_context.Command.Stderr}");
    }

    [Given(@"^the generated docs/report/index\.html and docs/\.nojekyll$")]
    public void GivenTheGeneratedPage()
    {
        _context.UseRepository();
        _context.Check = () => EvidenceChecks.PageViolations(_context.Root);
    }

    [When(@"^the page is read$")]
    public void WhenThePageIsRead() => _context.Result = EvidenceChecks.PageViolations(_context.Root);

    [Then(@"^it contains no ""<script"", contains ""joules-per-step\.svg"" and ""GPU-board energy only""$")]
    public void ThenThePageIsClean() => AssertNoViolations();

    [Given(@"^the committed content/linkedin-posts\.md$")]
    public void GivenTheCommittedPosts()
    {
        _context.UseRepository();
        _context.Check = () => EvidenceChecks.PostsViolations(_context.Root);
    }

    [Then(@"^it has three variants, no ""\{\{results\."" text, and a Sources line naming existing files$")]
    public void ThenThePostsAreResolved() => AssertNoViolations();

    [Given(@"^the committed content/demo-script\.md$")]
    public void GivenTheCommittedScript()
    {
        _context.UseRepository();
        _context.Check = () => EvidenceChecks.ShotListViolations(_context.Root);
    }

    [When(@"^the duration column is summed$")]
    public void WhenTheDurationsAreSummed() => _context.Result = EvidenceChecks.ShotListViolations(_context.Root);

    [Then(@"^the sum is 170 or less$")]
    public void ThenTheSumIsWithinTheLimit()
    {
        AssertNoViolations();
        int total = EvidenceChecks.ShotListSeconds(RepositoryRoot.ReadAllText("content/demo-script.md"));
        Assert.InRange(total, 1, EvidenceChecks.ShotListLimitSeconds);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"shot list totals {total} s"));
    }

    [Given(@"^a README whose results block has one number edited$")]
    public async Task GivenAReadmeWithAnEditedNumber()
    {
        TempWorkspace workspace = ReportFixture();
        await _context.RunCommandAsync(ReportCommand, "--in", "results/", "--out", "docs/report/", "--update-readme");
        Assert.True(_context.Command!.ExitCode == 0, $"update-readme: exit {_context.Command.ExitCode}; stderr: {_context.Command.Stderr}");

        string readme = File.ReadAllText(Path.Combine(workspace.Root, "README.md"));
        int begin = readme.IndexOf("<!-- results:begin -->", StringComparison.Ordinal);
        Match number = DecimalNumber().Match(readme, begin);
        Assert.True(number.Success, "the results block holds no decimal number to edit");
        char last = number.Value[^1];
        string edited = number.Value[..^1] + (char)('0' + (((last - '0') + 1) % 10));
        _ = workspace.WriteFile("README.md", readme[..number.Index] + edited + readme[(number.Index + number.Length)..]);
    }

    [When(@"^""report --verify-readme"" runs$")]
    public async Task WhenVerifyReadmeRuns() => await _context.RunCommandAsync(ReportCommand, "--verify-readme");

    [Then(@"^the exit code is 7$")]
    public void ThenTheExitCodeIsSeven() =>
        Assert.True(_context.Command!.ExitCode == 7, $"exit {_context.Command.ExitCode}; stdout: {_context.Command.Stdout}; stderr: {_context.Command.Stderr}");

    [Given(@"^a page fixture containing ""<script>""$")]
    public void GivenAPageWithAScript() => _ = PageFixture(noJekyll: true, "\n<script>alert(1)</script>\n");

    [Given(@"^a workspace without docs/\.nojekyll$")]
    public void GivenAWorkspaceWithoutNoJekyll() => _ = PageFixture(noJekyll: false, string.Empty);

    [Given(@"^a script whose durations sum to 171$")]
    public void GivenAScriptOf171Seconds()
    {
        TempWorkspace workspace = _context.NewFixture();
        _ = workspace.WriteFile("content/demo-script.md", "# Script\n\n| Shot | Start | Duration | On screen | Narration |\n|---|---|---|---|---|\n| 1 | 0 | 100 | a | b |\n| 2 | 100 | 71 | c | d |\n");
        _context.Check = () => EvidenceChecks.ShotListViolations(_context.Root);
    }

    [Given(@"^rendered posts containing ""(.+)""$")]
    public void GivenPostsWithAPlaceholder(string placeholder)
    {
        TempWorkspace workspace = _context.NewFixture();
        _ = workspace.WriteFile("content/linkedin-posts.md", RepositoryRoot.ReadAllText("content/linkedin-posts.md") + "\n" + placeholder + "\n");
        _context.Check = () => EvidenceChecks.PostsViolations(_context.Root);
    }

    [GeneratedRegex(@"[0-9]+\.[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalNumber();
}