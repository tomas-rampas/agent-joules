using System.Globalization;
using System.Text;

using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-154: the conformance evidence (the acceptance matrix, the Spec filter table and the scope of the repository). The Given step names the subject (the committed tree or a fixture) and the rule, the When step applies it,
/// the Then step reads the violations. Scoped to the story tag because "it fails with" belongs to every evidence story. Nothing here uses the network, a GPU or a key.
/// </summary>
[Binding]
[Scope(Tag = "AJ-S154")]
public sealed class EvidenceConformanceSteps(HardeningContext context)
{
    private readonly HardeningContext _context = context;

    // A matrix of 51 MET rows, one row left out when asked.
    private static string MatrixText(string? omit = null, string? extraRow = null)
    {
        StringBuilder text = new("# Build log\n\n## Acceptance matrix\n\n| ID | Command or artifact | Observed | Status | Reason |\n|---|---|---|---|---|\n");
        foreach (string id in EvidenceChecks.MatrixIds.Where(id => !string.Equals(id, omit, StringComparison.Ordinal)))
        {
            _ = text.Append(CultureInfo.InvariantCulture, $"| {id} | dotnet test --filter \"Spec={id}\" | passed | MET | |\n");
        }

        if (extraRow is not null)
        {
            _ = text.Append(extraRow).Append('\n');
        }

        return text.ToString();
    }

    private static string FilterText(string row) =>
        "# Build log\n\n## Spec filter results\n\n| ID | Passed | Failed | Skipped | Exit code |\n|---|---|---|---|---|\n" + row + "\n";

    private static int NumberAfter(string cell) => int.Parse(string.Concat(cell.Where(char.IsAsciiDigit)), CultureInfo.InvariantCulture);

    [Given(@"^the committed docs/build-log\.md$")]
    public void GivenTheCommittedBuildLog()
    {
        _context.UseRepository();
        _context.Text = RepositoryFiles.ReadOrNull("docs/build-log.md");
    }

    [When(@"^the Acceptance matrix is parsed$")]
    public void WhenTheMatrixIsParsed() => _context.Result = EvidenceChecks.AcceptanceMatrixViolations(_context.Text);

    [Then(@"^it has 51 rows with no empty status$")]
    public void ThenItHas51Rows()
    {
        Assert.True(_context.Violations.Count == 0, string.Join("\n", _context.Violations));
        Assert.Equal(51, EvidenceChecks.AcceptanceMatrixRowIds(_context.Text!).Count);
    }

    [When(@"^the Spec filter results table is parsed$")]
    public void WhenTheFilterTableIsParsed() => _context.Result = EvidenceChecks.SpecFilterViolations(_context.Text);

    [Then(@"^it has 36 rows each with passed 1 or more and failed 0$")]
    public void ThenItHas36Rows()
    {
        Assert.True(_context.Violations.Count == 0, string.Join("\n", _context.Violations));
        Assert.Equal(36, EvidenceChecks.SpecFilterRowIds(_context.Text!).Count);
    }

    [Given(@"^the committed Directory\.Packages\.props$")]
    public void GivenTheCommittedPackages()
    {
        _context.UseRepository();
        _context.Check = () => EvidenceChecks.PackageScopeViolations(_context.Root);
    }

    [When(@"^it is searched for alpha, preview, charting and Semantic Kernel packages$")]
    public void WhenItIsSearched() => _context.Result = (_context.Check ?? throw new Xunit.Sdk.XunitException("No rule was chosen by a Given step."))();

    [Then(@"^nothing is found$")]
    public void ThenNothingIsFound() => Assert.True(_context.Violations.Count == 0, string.Join("\n", _context.Violations));

    [Given(@"^a build log whose Acceptance matrix lacks (EDGE-[0-9]{3})$")]
    public void GivenAMatrixWithoutARow(string id) =>
        _context.Check = () => EvidenceChecks.AcceptanceMatrixViolations(MatrixText(omit: id));

    [Given(@"^a filter table row ""([^""]*)""$")]
    public void GivenAFilterTableRow(string row)
    {
        string[] cells = [.. row.Split('|', StringSplitOptions.TrimEntries)];
        int passed = NumberAfter(cells[1]);
        int failed = NumberAfter(cells[2]);
        string text = FilterText(string.Create(CultureInfo.InvariantCulture, $"| {cells[0]} | {passed} | {failed} | 0 | {(failed > 0 ? 1 : 0)} |"));
        _context.Check = () => EvidenceChecks.SpecFilterViolations(text);
    }

    [Given(@"^a matrix row ""([^""]*)"" with an empty reason$")]
    public void GivenAMatrixRowWithoutReason(string row)
    {
        string[] cells = [.. row.Split('|', StringSplitOptions.TrimEntries)];
        string id = cells[0];
        string status = cells[^1];
        string text = MatrixText(omit: id, extraRow: $"| {id} | {cells[1]} | observed | {status} | |");
        _context.Check = () => EvidenceChecks.AcceptanceMatrixViolations(text);
    }

    [When(@"^the matrix check runs$")]
    [When(@"^the scope check runs$")]
    public void WhenTheCheckRuns() => _context.Result = (_context.Check ?? throw new Xunit.Sdk.XunitException("No rule was chosen by a Given step."))();

    [Given(@"^a Directory\.Packages\.props that references ([A-Za-z0-9.]+)$")]
    public void GivenAPackagesFileWithAPackage(string package)
    {
        TempWorkspace workspace = _context.NewFixture();
        _ = workspace.WriteFile("Directory.Packages.props", "<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"" + package + "\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>\n");
        _context.Check = () => EvidenceChecks.ScopeViolations(_context.Root, uncheckedBoxes: 0);
    }

    [Given(@"^a git history with a commit ""([^""]*)"" and one unchecked box$")]
    public void GivenAGitHistoryWithAStretchCommit(string subject)
    {
        Assert.True(GitRunner.Available(), "git is not on PATH");
        TempWorkspace workspace = _context.NewFixture();
        Assert.Equal(0, GitRunner.Run(workspace.Root, "init", "--quiet", workspace.Root).ExitCode);
        Assert.Equal(0, GitRunner.Run(workspace.Root, "config", "user.name", "Spec Fixture").ExitCode);
        Assert.Equal(0, GitRunner.Run(workspace.Root, "config", "user.email", "fixture@example.invalid").ExitCode);
        Assert.Equal(0, GitRunner.Run(workspace.Root, "config", "commit.gpgsign", "false").ExitCode);
        Assert.Equal(0, EvidenceChecks.CommitWithoutHooks(workspace.Root, subject).ExitCode);
        _context.UncheckedBoxes = 1;
        _context.Check = () => EvidenceChecks.ScopeViolations(_context.Root, _context.UncheckedBoxes);
    }

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure) => Assert.Contains(failure, _context.Violations);
}