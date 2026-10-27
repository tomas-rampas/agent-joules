using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-158: the submission order, the timeline offsets and the cut-off plan of <c>docs/devpost-checklist.md</c>. Scoped to the story tag because sentences such as "it fails with" and "the checklist check runs" belong to
/// several documentation stories. The Given step names the subject (the committed file or a fixture with one defect built by <see cref="SubmissionFixtures"/>), the When step applies the rule and the Then step reads
/// the violations. The cloudgpu-absent scenario runs the two commands of the cloudgpu row in process on a fixture workspace.
/// </summary>
[Binding]
[Scope(Tag = "AJ-S158")]
public sealed class SubmissionPlanSteps(SubmissionContext context)
{
    private const string ChecklistFile = "docs/devpost-checklist.md";
    private const string CloudGpuNotRun = "cloudgpu | not run";

    private static readonly string[] GenerateTokens = ["report", "--in", "results/", "--out", "docs/report/", "--update-readme", "--render-content"];

    private readonly SubmissionContext _context = context;

    private string Checklist => EvidenceChecks.ReadText(_context.Root, ChecklistFile) ?? throw new Xunit.Sdk.XunitException("missing " + ChecklistFile);

    private void AssertNoViolations() =>
        Assert.True(_context.Violations.Count == 0, string.Join("\n", _context.Violations));

    private void UseChecklistFixture(Func<string, string> edit)
    {
        TempWorkspace workspace = _context.NewFixture();
        _ = workspace.WriteFile(ChecklistFile, edit(SubmissionFixtures.Checklist()));
        _context.Check = () => EvidenceChecks.SubmissionPlanViolations(_context.Root);
    }

    [Given(@"^the committed docs/devpost-checklist\.md$")]
    [Given(@"^the Timeline section$")]
    [Given(@"^the Cut-off plan table$")]
    public void GivenTheCommittedChecklist() => _context.UseRepository();

    [When(@"^the Submission order section is parsed$")]
    public void WhenTheOrderIsParsed() => _context.Result = EvidenceChecks.SubmissionOrderViolations(Checklist);

    [Then(@"^it has 12 numbered items each ending with ""\(user\)"" in the specified order$")]
    public void ThenTheOrderHoldsTwelveUserActions() => AssertNoViolations();

    [When(@"^each UTC and PDT pair is compared$")]
    public void WhenThePairsAreCompared() => _context.Result = EvidenceChecks.TimelineViolations(Checklist);

    [Then(@"^PDT equals UTC minus 7 hours for every pair and the last target is at least 3 hours before 17:00 UTC$")]
    public void ThenTheOffsetsAreCorrect() => AssertNoViolations();

    [When(@"^its rows are read$")]
    public void WhenTheRowsAreRead() => _context.Result = EvidenceChecks.CutOffViolations(Checklist, EvidenceChecks.ReadText(_context.Root, "content/demo-script.md"));

    [Then(@"^rows exist for cloudgpu, local, nvidia, nebius, judge, Pages, video and feedback$")]
    public void ThenEveryItemHasARow() =>
        Assert.True(_context.Violations.All(static violation => !violation.StartsWith("no cut-off row", StringComparison.Ordinal) && !violation.StartsWith("missing section", StringComparison.Ordinal)), string.Join("\n", _context.Violations));

    [Then(@"^each row names files, a regeneration command, a fallback story and unchecked boxes$")]
    public void ThenEveryRowIsComplete() => AssertNoViolations();

    [Given(@"^a fixture workspace with run files for local, nvidia and nebius$")]
    public void GivenAFixtureWithoutCloudGpu()
    {
        TempWorkspace workspace = _context.NewFixture();
        foreach (string tier in EvidenceChecks.PublishedTiers)
        {
            string name = EvidenceChecks.NewestRunFileName(RepositoryRoot.Path, tier) ?? throw new Xunit.Sdk.XunitException("results/ holds no run file for tier " + tier);
            _ = workspace.WriteFile("results/" + name, RepositoryRoot.ReadAllText("results/" + name));
        }

        // The cloudgpu run file and its probe and calibration files are left out. The report reads Report:RepositoryUrl from the configuration file; the README needs its result markers.
        _ = workspace.WriteFile("config/appsettings.json", RepositoryRoot.ReadAllText("config/appsettings.json"));
        _ = workspace.WriteFile("README.md", ReadmeFixtures.WithMarkers(RepositoryRoot.ReadAllText("README.md")));
        foreach (string path in Directory.EnumerateFiles(RepositoryRoot.Resolve("content")).Where(static path => !string.Equals(Path.GetFileName(path), "linkedin-posts.md", StringComparison.Ordinal)))
        {
            _ = workspace.WriteFile("content/" + Path.GetFileName(path), File.ReadAllText(path));
        }

        // The committed template carries cloudgpu placeholders, which need a cloudgpu run file; the absent state (AJ-129) uses wording without them, so the sentence that holds them is cut here.
        string template = RepositoryRoot.ReadAllText("content/linkedin-posts.template.md");
        _ = workspace.WriteFile("content/linkedin-posts.template.md", string.Join('\n', template.Split('\n').Select(static line => line.Contains("{{results.cloudgpu", StringComparison.Ordinal) ? line[..line.IndexOf(" One Nebius AI Cloud", StringComparison.Ordinal)] : line)));
    }

    [When(@"^the commands of the cloudgpu row are run$")]
    public async Task WhenTheCloudGpuCommandsAreRun()
    {
        await _context.RunCommandAsync(GenerateTokens);
        Assert.True(_context.Command!.ExitCode == 0, $"generate: exit {_context.Command.ExitCode}; stdout: {_context.Command.Stdout}; stderr: {_context.Command.Stderr}");
    }

    [Then(@"^the README block and summary contain ""cloudgpu \| not run"" and ""report --verify-readme"" exits 0$")]
    public async Task ThenTheNotRunRowIsGenerated()
    {
        Assert.Contains(CloudGpuNotRun, File.ReadAllText(Path.Combine(_context.Root, "README.md")), StringComparison.Ordinal);
        Assert.Contains(CloudGpuNotRun, File.ReadAllText(Path.Combine(_context.Root, "docs", "report", "summary.md")), StringComparison.Ordinal);

        await _context.RunCommandAsync("report", "--verify-readme");
        Assert.True(_context.Command!.ExitCode == 0, $"verify-readme: exit {_context.Command.ExitCode}; stdout: {_context.Command.Stdout}; stderr: {_context.Command.Stderr}");
    }

    [Given(@"^a Timeline line ""([^""]+)""$")]
    public void GivenATimelineLine(string line) =>
        UseChecklistFixture(text => SubmissionFixtures.Change(text, "## Timeline\n\n", "## Timeline\n\n- " + line + "\n"));

    [Given(@"^a Submission order item without ""\(user\)""$")]
    public void GivenAnItemWithoutTheMarker() =>
        UseChecklistFixture(static text => SubmissionFixtures.Change(text, "11. Submit (user)", "11. Submit"));

    [Given(@"^the cloudgpu row with the command column empty$")]
    public void GivenACloudGpuRowWithoutACommand() =>
        UseChecklistFixture(static text => string.Join('\n', text.Split('\n').Select(static line => line.StartsWith("| cloudgpu tier |", StringComparison.Ordinal)
            ? "| " + string.Join(" | ", EvidenceChecks.CellsOf(line).Select(static (cell, index) => index == 3 ? string.Empty : cell.Replace("|", "\\|", StringComparison.Ordinal))) + " |"
            : line)));

    [Given(@"^a checklist without ""2026-10-30 10:00 PDT \(17:00 UTC\)""$")]
    public void GivenAChecklistWithoutTheDeadline() =>
        UseChecklistFixture(static text => SubmissionFixtures.Change(text, "Deadline: 2026-10-30 10:00 PDT (17:00 UTC)", "Deadline: the end of October"));

    [Given(@"^a checklist sentence ""([^""]+)""$")]
    public void GivenAChecklistSentence(string sentence) =>
        UseChecklistFixture(text => text + "\n" + sentence + ".\n");

    [When(@"^the checklist check runs$")]
    public void WhenTheChecklistCheckRuns() => _context.RunCheck();

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure) => Assert.Contains(failure, _context.Violations);
}