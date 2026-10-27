using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-159: the drop order, the never-dropped list and the buffer of <c>docs/build-log.md</c>. Scoped to the story tag because sentences such as "it fails with" belong to several documentation stories. The Given step
/// names the subject (the committed build log or a fixture with one defect built by <see cref="SubmissionFixtures"/>), the When step applies the rule and the Then step reads the violations.
/// </summary>
[Binding]
[Scope(Tag = "AJ-S159")]
public sealed class DropOrderSteps(SubmissionContext context)
{
    private const string BuildLogFile = "docs/build-log.md";

    private readonly SubmissionContext _context = context;

    private string BuildLog => EvidenceChecks.ReadText(_context.Root, BuildLogFile) ?? throw new Xunit.Sdk.XunitException("missing " + BuildLogFile);

    private void AssertNoViolations() =>
        Assert.True(_context.Violations.Count == 0, string.Join("\n", _context.Violations));

    private void UseBuildLogFixture(Func<string, string> edit)
    {
        TempWorkspace workspace = _context.NewFixture();
        _ = workspace.WriteFile(BuildLogFile, edit(SubmissionFixtures.BuildLog()));
        _context.Check = () => [.. EvidenceChecks.DropOrderMonotonicViolations(BuildLog), .. EvidenceChecks.DropTriggerViolations(BuildLog), .. EvidenceChecks.NeverDroppedViolations(BuildLog), .. EvidenceChecks.BufferViolations(BuildLog)];
    }

    [Given(@"^the committed docs/build-log\.md$")]
    [Given(@"^the Drop order and Buffer sections$")]
    [Given(@"^the committed build log$")]
    [Given(@"^the Buffer table$")]
    public void GivenTheCommittedBuildLog() => _context.UseRepository();

    [When(@"^the Drop order list is parsed$")]
    public void WhenTheListIsParsed() => _context.Result = EvidenceChecks.DropOrderMonotonicViolations(BuildLog);

    [Then(@"^steps C1 to C9 and D1 to D3 have ""boxes unticked"" none and Level 3 names boxes 21, 22 and 23$")]
    public void ThenTheOrderIsMonotonic() => AssertNoViolations();

    // The risk links of criterion 5 are read here too, with the register of the story file of the same tree.
    [When(@"^each trigger time is read$")]
    public void WhenTheTriggersAreRead()
    {
        string? story = EvidenceChecks.ReadText(_context.Root, "docs/plan/stories/M7-hardening-and-submission.md");
        _context.Result = [.. EvidenceChecks.DropTriggerViolations(BuildLog), .. EvidenceChecks.DropRiskViolations(BuildLog, story is null ? null : EvidenceChecks.StoryRiskIds(story))];
    }

    [Then(@"^every time is before 2026-10-30 17:00 UTC$")]
    public void ThenEveryTriggerIsBeforeTheDeadline() => AssertNoViolations();

    [When(@"^the Never dropped list is read$")]
    public void WhenTheNeverDroppedListIsRead() => _context.Result = EvidenceChecks.NeverDroppedViolations(BuildLog);

    [Then(@"^it names the nebius run file, the local run file, the secret scan, the README block, the checklist with the push command, the story, the script and the user's video and submission$")]
    public void ThenTheMinimumIsNamed() => AssertNoViolations();

    [When(@"^the last target and the deadline are compared$")]
    public void WhenTheBufferIsCompared() => _context.Result = EvidenceChecks.BufferViolations(BuildLog);

    [Then(@"^the gap is 3 hours or more$")]
    public void ThenTheGapIsThreeHours() => AssertNoViolations();

    [Given(@"^a drop list where Level 3 precedes step C3$")]
    public void GivenLevel3BeforeC3() =>
        UseBuildLogFixture(static text => SubmissionFixtures.Change(text, "| C3 |", "Level 3: the cloudgpu tier unticks boxes 21, 22 and 23. RK-5.\n| C3 |"));

    [Given(@"^a drop item with an empty trigger$")]
    public void GivenAnEmptyTrigger() =>
        UseBuildLogFixture(static text => SubmissionFixtures.ChangeRow(text, "C4", "2026-10-26 17:00 UTC and S > 3 h", string.Empty));

    [Given(@"^a drop item with trigger ""([^""]+)""$")]
    public void GivenATrigger(string trigger) =>
        UseBuildLogFixture(text => SubmissionFixtures.ChangeRow(text, "C5", "2026-10-26 17:00 UTC and S > 4 h", trigger));

    [Given(@"^a drop list that contains the secret scan$")]
    public void GivenADropListWithTheSecretScan() =>
        UseBuildLogFixture(static text => SubmissionFixtures.ChangeRow(text, "C2", "a requirement is reported UNMET", "the secret scan is skipped"));

    [Given(@"^a Buffer table whose last target is 15:00 UTC$")]
    public void GivenABufferOf15() =>
        UseBuildLogFixture(static text => SubmissionFixtures.Change(text, "| submission complete | 2026-10-30 14:00 UTC | 3 |", "| submission complete | 2026-10-30 15:00 UTC | 2 |"));

    [When(@"^the order check runs$")]
    public void WhenTheOrderCheckRuns() => _context.RunCheck();

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure) => Assert.Contains(failure, _context.Violations);
}