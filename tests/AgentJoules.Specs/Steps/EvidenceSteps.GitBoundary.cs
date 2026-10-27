using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-156: the git history and the push boundary. The hermetic scenarios build a temporary repository with a local identity and no hook, and stage origin/main with a bare remote inside the temporary folder
/// (cloned from the work repository and fetched), so nothing leaves the machine and nothing is pushed. The checklist scenario reads the committed file. The @integration scenarios are gated by
/// <c>AGENTJOULES_INTEGRATION</c>: the one about the live remote asks <c>git ls-remote origin main</c> and requires the printed hash to be a commit that HEAD descends from (so it stays green after the exit publication of main);
/// the one about a moved remote reads a bare remote inside the temporary folder, and the question for the user is written to the scenario output, never asked.
/// </summary>
[Binding]
[Scope(Tag = "AJ-S156")]
public sealed class EvidenceGitBoundarySteps(GitBoundaryContext context, IReqnrollOutputHelper output)
{
    private readonly GitBoundaryContext _context = context;
    private readonly IReqnrollOutputHelper _output = output;

    private bool Skipped()
    {
        if (_context.SkipNote is null)
        {
            return false;
        }

        _output.WriteLine("passed without asserting: " + _context.SkipNote);
        return true;
    }

    [Given(@"^a temporary repository with a recorded build-start and commits ""([^""]*)"", ""([^""]*)"" and ""([^""]*)""$")]
    public void GivenARepositoryWithCommits(string first, string second, string third)
    {
        _ = _context.NewRepository("planning commit before the build");
        _context.Commit(first);
        _context.Commit(second);
        _context.Commit(third);
        _context.Check = () => EvidenceChecks.HistoryViolations(_context.Root, _context.Recorded);
    }

    [Given(@"^a temporary repository with a commit ""([^""]*)""$")]
    public void GivenARepositoryWithACommit(string subject)
    {
        _ = _context.NewRepository("planning commit before the build");
        _context.Commit(subject);
        _context.Check = () => EvidenceChecks.HistoryViolations(_context.Root, _context.Recorded);
    }

    [Given(@"^a temporary repository with origin/main at the recorded base and three local commits$")]
    public void GivenOriginAtTheBase()
    {
        _ = _context.NewRepository("AJ-001: recorded base");
        _context.AttachOrigin();
        _context.Commit("AJ-002: first local commit");
        _context.Commit("AJ-003 REQ-001: second local commit");
        _context.Commit("AJ-004: third local commit");
        UseBoundaryCheck();
    }

    [Given(@"^a temporary repository where origin/main equals HEAD$")]
    public void GivenOriginEqualsHead()
    {
        _ = _context.NewRepository("AJ-001: recorded base");
        _context.Commit("AJ-002: first local commit");
        _context.Commit("AJ-003: second local commit");
        _context.AttachOrigin();
        UseBoundaryCheck();
    }

    [Given(@"^a temporary repository with a modified tracked file$")]
    public void GivenAModifiedTrackedFile()
    {
        _ = _context.NewRepository("AJ-001: recorded base");
        _context.Write("tracked.txt", "one\n");
        _ = _context.Git("add", "tracked.txt");
        _context.Commit("AJ-002: add the tracked file");
        _context.Write("tracked.txt", "two\n");
        _context.Check = () => EvidenceChecks.CleanTreeViolations(_context.Root);
    }

    [Given(@"^the committed docs/devpost-checklist\.md$")]
    public void GivenTheCommittedChecklist()
    {
        _context.Text = RepositoryRoot.ReadAllText("docs/devpost-checklist.md");
        _context.Check = () => EvidenceChecks.ChecklistPushViolations(_context.Text);
    }

    [Given(@"^a copy of the checklist without ""([^""]*)""$")]
    public void GivenAChecklistWithout(string text)
    {
        _context.Text = RepositoryRoot.ReadAllText("docs/devpost-checklist.md").Replace(text, "git pull", StringComparison.Ordinal);
        Assert.DoesNotContain(text, _context.Text, StringComparison.Ordinal);
        _context.Check = () => EvidenceChecks.ChecklistPushViolations(_context.Text);
    }

    [When(@"^the history check runs$")]
    [When(@"^the push-boundary check runs$")]
    [When(@"^the gate precondition check runs$")]
    public void WhenTheCheckRuns() => _context.Result = (_context.Check ?? throw new XunitException("No rule was chosen by a Given step."))();

    [When(@"^it is searched for ""([^""]*)""$")]
    public void WhenSearchedFor(string text)
    {
        Assert.Equal("git push", text);
        WhenTheCheckRuns();
    }

    [Then(@"^it passes$")]
    public void ThenItPasses() => Assert.True(_context.Violations.Count == 0, string.Join("\n", _context.Violations));

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string text) => Assert.True(_context.Violations.Contains(text, StringComparer.Ordinal), "expected: " + text + "\nactual:\n" + string.Join("\n", _context.Violations));

    [Then(@"^it reports ([0-9]+) commits ahead and passes$")]
    public void ThenItReportsAhead(int count)
    {
        ThenItPasses();
        Assert.Equal(count, _context.Boundary!.Ahead);
        Assert.Contains(count + " commits ahead", _context.Boundary.Output, StringComparison.Ordinal);

        // The fallback to the recorded reference must not mask a missing origin: origin/main was tracked and is the bare remote's main.
        Assert.DoesNotContain("is not tracked", _context.Boundary.Output, StringComparison.Ordinal);
        string bareMain = GitRunner.Run(_context.OriginPath, "rev-parse", "--verify", "refs/heads/main").Stdout.Trim();
        Assert.NotEmpty(bareMain);
        Assert.Equal(bareMain, _context.Boundary.OriginHash);
    }

    [Then(@"^at least one fenced block contains it$")]
    public void ThenAFencedBlockContainsIt()
    {
        ThenItPasses();
        Assert.Empty(EvidenceChecks.UserActionLabelViolations(_context.Text!));
    }

    [Given(@"^network access and the recorded base hash$")]
    public void GivenNetworkAccess()
    {
        _context.OpenGate();
        _context.Recorded = EvidenceChecks.RemoteReferenceHash;
    }

    [When(@"^""git ls-remote origin main"" runs$")]
    public void WhenLsRemoteRuns()
    {
        if (Skipped())
        {
            return;
        }

        // Read-only: ls-remote asks the remote for its main and sends nothing.
        GitResult result = GitRunner.Run(RepositoryRoot.Path, "ls-remote", "origin", "refs/heads/main");
        Assert.True(result.ExitCode == 0, "git ls-remote exited " + result.ExitCode + ": " + result.Stderr.Trim());
        _context.Text = result.Stdout;
        _output.WriteLine("git ls-remote origin main: " + result.Stdout.Trim());
    }

    [Then(@"^it prints the recorded hash$")]
    public void ThenItPrintsTheRecordedHash()
    {
        if (Skipped())
        {
            return;
        }

        // The printed hash is the remote main; it satisfies the scenario when this checkout holds it and HEAD descends from it.
        string printed = EvidenceChecks.RemoteMainHash(_context.Text ?? string.Empty);
        _output.WriteLine("the remote main " + printed + " is checked as an ancestor of HEAD (the recorded base " + _context.Recorded + " names the previous milestone's exit commit)");
        IReadOnlyList<string> violations = EvidenceChecks.RemoteMainViolations(RepositoryRoot.Path, _context.Text ?? string.Empty);
        Assert.True(violations.Count == 0, "the remote main is not an ancestor of HEAD: " + (_context.Text ?? string.Empty).Trim());
    }

    [Given(@"^the remote main differs from the recorded base$")]
    public void GivenTheRemoteMainDiffers()
    {
        // A bare remote inside the temporary folder holds one commit more than the recorded base; no network is needed.
        _ = _context.NewRepository("AJ-001: recorded base");
        _context.Commit("AJ-002: a commit the remote has and the base has not");
        _context.AttachOrigin();
        string recorded = _context.Git("rev-parse", "HEAD~1").Trim();
        _context.Recorded = recorded;

        // The work repository goes back to the recorded base: the remote main is then a commit HEAD does not descend from.
        _ = _context.Git("reset", "--quiet", "--hard", recorded);
        _context.Check = () =>
        {
            GitResult result = GitRunner.Run(_context.Root, "ls-remote", "origin", "refs/heads/main");
            return result.ExitCode == 0 ? EvidenceChecks.RemoteMainViolations(_context.Root, result.Stdout) : ["remote main moved"];
        };
    }

    [When(@"^the check runs$")]
    public void WhenTheCheckRunsAlone() => WhenTheCheckRuns();

    [Then(@"^it reports ""([^""]*)"" and asks the user whether they pushed$")]
    public void ThenItReportsAndAsks(string text)
    {
        ThenItFailsWith(text);
        // The step records the question; nothing interactive is asked.
        _output.WriteLine(EvidenceChecks.MovedQuestion);
        Assert.StartsWith(text, EvidenceChecks.MovedQuestion, StringComparison.Ordinal);
    }

    private void UseBoundaryCheck() =>
        _context.Check = () =>
        {
            _context.Boundary = EvidenceChecks.BoundaryCheck(_context.Root, _context.Recorded);
            _output.WriteLine(_context.Boundary.Output);
            return _context.Boundary.Violations;
        };
}