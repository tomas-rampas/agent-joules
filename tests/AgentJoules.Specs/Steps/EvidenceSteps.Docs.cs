using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>AJ-153: the four reference documents and the three Devpost files.</summary>
public sealed partial class EvidenceSteps
{
    private const string ChecklistFile = "docs/devpost-checklist.md";

    private static readonly string[] DocFiles =
    [
        "docs/nim-local-attempt.md",
        "docs/endpoints.md",
        "docs/methodology.md",
        ChecklistFile,
        "docs/devpost-story.md",
        "docs/devpost-feedback.md",
    ];

    // A fixture folder with a copy of the six documents; the edit changes the text of one of them.
    private void DocsFixture(string changed, Func<string, string> edit)
    {
        TempWorkspace workspace = _context.NewFixture();
        foreach (string file in DocFiles)
        {
            _ = workspace.WriteFile(file, string.Equals(file, changed, StringComparison.Ordinal) ? edit(RepositoryRoot.ReadAllText(file)) : RepositoryRoot.ReadAllText(file));
        }
    }

    [Given(@"^the four committed documents$")]
    public void GivenTheFourDocuments()
    {
        _context.UseRepository();
        _context.Check = () => EvidenceChecks.VerificationDates(_context.Root).Violations;
    }

    [When(@"^the ""Verified:"" lines are read$")]
    public void WhenTheVerifiedLinesAreRead() => _context.Result = EvidenceChecks.VerificationDates(_context.Root).Violations;

    [Then(@"^each document has a date of 2026-10-17 or later$")]
    public void ThenEachDocumentIsDated() => AssertNoViolations();

    [Given(@"^the committed docs/devpost-checklist\.md$")]
    public void GivenTheCommittedChecklist()
    {
        _context.UseRepository();
        _context.Check = () => EvidenceChecks.ChecklistViolations(_context.Root);
    }

    [Then(@"^it has one ""Demo URL:"" line and the headings Registration, Tracks, Judging criteria, Credits$")]
    public void ThenTheChecklistIsComplete() => AssertNoViolations();

    [Given(@"^the committed docs/devpost-story\.md$")]
    public void GivenTheCommittedStory()
    {
        _context.UseRepository();
        _context.Check = () => EvidenceChecks.StoryViolations(_context.Root);
    }

    [When(@"^placeholders and headings are counted$")]
    public void WhenPlaceholdersAreCounted() => _context.Result = EvidenceChecks.StoryViolations(_context.Root);

    [Then(@"^""\[\[FILL:"" occurs 0 times and no heading contains ""Fill-after-build""$")]
    public void ThenTheStoryIsFinished() => AssertNoViolations();

    [Given(@"^the committed feedback file and checklist$")]
    public void GivenTheCommittedFeedbackAndChecklist()
    {
        _context.UseRepository();
        _context.Check = () => [.. EvidenceChecks.FeedbackViolations(_context.Root), .. EvidenceChecks.ChecklistViolations(_context.Root)];
    }

    [When(@"^the owner markers and the checklist heading are read$")]
    public void WhenTheOwnerMarkersAreRead() => _context.Result = _context.Check!();

    [Then(@"^""owner: builder"" occurs 0 times, ""owner: user"" occurs 7 times and the checklist lists seven bullets$")]
    public void ThenTheFeedbackIsListed() => AssertNoViolations();

    [Given(@"^a copy of docs/methodology\.md without a ""Verified:"" line$")]
    public void GivenMethodologyWithoutADate()
    {
        DocsFixture("docs/methodology.md", static text => string.Join('\n', DocumentChecks.Lines(text).Where(static line => !line.StartsWith("Verified:", StringComparison.Ordinal))));
        UseDocsCheck();
    }

    [Given(@"^a checklist whose line reads ""(Verified: not verified [^""]+)""$")]
    public void GivenANotVerifiedChecklist(string line)
    {
        DocsFixture(ChecklistFile, text => string.Join('\n', DocumentChecks.Lines(text).Select(candidate => candidate.StartsWith("Verified:", StringComparison.Ordinal) ? line : candidate)));
        UseDocsCheck();
    }

    [Given(@"^a copy of the story with one ""\[\[FILL:"" left$")]
    public void GivenAStoryWithAPlaceholder()
    {
        DocsFixture("docs/devpost-story.md", static text => text + "\n[[FILL: the missing answer]]\n");
        UseDocsCheck();
    }

    [Given(@"^a copy of the feedback file with one ""owner: builder"" placeholder$")]
    public void GivenFeedbackWithABuilderPlaceholder()
    {
        DocsFixture("docs/devpost-feedback.md", static text => text + "\nPlaceholder (owner: builder)\n");
        UseDocsCheck();
    }

    [Given(@"^a copy of the checklist without ""Feedback form, user fills""$")]
    public void GivenAChecklistWithoutTheFeedbackHeading()
    {
        DocsFixture(ChecklistFile, static text => text.Replace("## Feedback form, user fills", "## Feedback form", StringComparison.Ordinal));
        UseDocsCheck();
    }

    [When(@"^the documentation check runs$")]
    public void WhenTheDocumentationCheckRuns()
    {
        DocsEvidenceResult result = EvidenceChecks.DocsEvidence(_context.Root);
        _context.Result = result.Violations;
        _context.UncheckedBoxes = result.UncheckedBoxes;
    }

    [Then(@"^box 9 is reported unchecked with the reason ""([^""]+)""$")]
    public void ThenBoxNineIsUnchecked(string reason)
    {
        Assert.Contains(_context.UncheckedBoxes, box => box.Contains("box 9", StringComparison.Ordinal) && box.Contains(reason, StringComparison.Ordinal));
        Assert.DoesNotContain(_context.Violations, static violation => violation.Contains("devpost-checklist", StringComparison.Ordinal) && violation.Contains("verification", StringComparison.Ordinal));
    }

    // The documentation scenarios run the whole documentation rule; the Check delegate is set only so a misplaced "the evidence check runs" fails loudly.
    private void UseDocsCheck() => _context.Check = () => EvidenceChecks.DocsEvidence(_context.Root).Violations;
}