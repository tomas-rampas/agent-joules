using AgentJoules.Specs.Support;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-142: the Devpost feedback answers (REQ-028). Scoped to the feature because sentences such as "it fails with" and "the feedback check runs" belong to several documentation stories. The committed files are read through
/// <see cref="DevpostCommitted"/>; the pre-fill feedback file is read from git by <see cref="PreFillFiles"/> (a committed fixture, compared with the git object when git can read it). The negative
/// scenarios mutate a copy in memory, and the baseline rule is modelled with a flag, so no file is written.
/// </summary>
[Binding]
[Scope(Feature = "AJ-142 Devpost feedback answers")]
public sealed class DevpostFeedbackSteps(DevpostContext context)
{
    private readonly DevpostContext _context = context;

    private IReadOnlyList<string> CheckAll(string feedback, string checklist, string buildLog) =>
        DocumentChecks.FeedbackChecks(feedback, _context.PreFill!, checklist, buildLog, RepositoryFiles.Exists, DevpostCommitted.BaselineRunExists());

    [Given(@"^the committed docs/devpost-feedback\.md and its pre-fill version$")]
    [Given(@"^the eight filled passages$")]
    public void GivenTheCommittedFeedbackAndItsPreFill()
    {
        _context.Text = DevpostCommitted.Feedback();
        _context.BuildLog = DevpostCommitted.BuildLog();
        _context.PreFill = PreFillFiles.Feedback();
    }

    [Given(@"^the committed docs/devpost-checklist\.md$")]
    public void GivenTheCommittedChecklist() => _context.Checklist = DevpostCommitted.Checklist();

    [Given(@"^a workspace with no baseline run file$")]
    public void GivenNoBaselineFile()
    {
        _context.Text = DevpostCommitted.Feedback();

        // The repository holds none either, so the committed passage is the one the rule asks for.
        Assert.False(DevpostCommitted.BaselineRunExists());
    }

    [Given(@"^a copy of the feedback file with one ""owner: builder"" placeholder left$")]
    public void GivenACopyWithABuilderPlaceholder()
    {
        GivenTheCommittedFeedbackAndItsPreFill();
        _context.Text = _context.Text.Replace("owner: builder", "owner: done", StringComparison.Ordinal) + "\n[[FILL: a value | source: a file | owner: builder]]\n";
    }

    [Given(@"^a copy of the feedback file where the Q6 user placeholder is replaced by ""([^""]*)""$")]
    public void GivenACopyWithQ6Filled(string value)
    {
        GivenTheCommittedFeedbackAndItsPreFill();
        string[] lines = DocumentChecks.Lines(_context.Text);
        int at = Array.FindIndex(lines, static line => line.Contains("owner: user", StringComparison.Ordinal) && line.Contains("recommend", StringComparison.OrdinalIgnoreCase));
        Assert.True(at >= 0, "The Q6 user placeholder is not in the feedback file.");
        lines[at] = value;
        _context.Text = string.Join('\n', lines);
    }

    [Given(@"^a copy of the checklist without ""([^""]*)""$")]
    public void GivenACopyOfTheChecklistWithoutTheHeading(string heading)
    {
        GivenTheCommittedFeedbackAndItsPreFill();
        _context.Checklist = DocumentChecks.WithoutHeading(DevpostCommitted.Checklist(), heading);
        Assert.NotNull(DocumentChecks.HasHeadingAtAnyLevel(_context.Checklist, heading));
    }

    [Given(@"^a feedback file claiming the credits covered all runs and no user statement in docs/build-log\.md$")]
    public void GivenAnInventedCreditsAnswer()
    {
        GivenTheCommittedFeedbackAndItsPreFill();
        string[] lines = DocumentChecks.Lines(_context.Text);
        int q6 = Array.FindIndex(lines, static line => line.StartsWith("## Q6.", StringComparison.Ordinal));
        Assert.True(q6 > 0, "The feedback file has no Q6 heading.");
        _context.Text = string.Join('\n', [.. lines[..q6], "The hackathon credits covered all the Nebius runs.", string.Empty, .. lines[q6..]]);
        _context.BuildLog = string.Empty;
    }

    [Given(@"^a feedback file claiming the credits covered all runs and the committed docs/build-log.md$")]
    public void GivenAnInventedCreditsAnswerAgainstTheCommittedLog()
    {
        GivenAnInventedCreditsAnswer();
        _context.BuildLog = DevpostCommitted.BuildLog();
    }

    [Given(@"^a build log with a prose line quoting the credits marker mid-sentence$")]
    public void GivenAProseLineQuotingTheCreditsMarker() =>
        _context.BuildLog += "\nThe review noted that the User statement on credits: the user confirmed coverage sentence was never written.\n";

    [Given(@"^the Q5 credits claim names its source and label$")]
    public void GivenTheCreditsClaimNamesItsSourceAndLabel()
    {
        const string claim = "The hackathon credits covered all the Nebius runs.";
        Assert.Contains(claim, _context.Text, StringComparison.Ordinal);
        _context.Text = _context.Text.Replace(claim, "The hackathon credits covered all the Nebius runs (measured; docs/build-log.md).", StringComparison.Ordinal);
    }

    [Given(@"^a build log holding the user statement on credits list item$")]
    public void GivenABuildLogHoldingTheCreditsStatement() =>
        _context.BuildLog += "\n- User statement on credits: the user reported on 2026-10-26 that the Token Factory credits covered the hosted runs\n";

    [Given(@"^a copy of the Q7 passage with ""([^""]*)"" and no results file$")]
    public void GivenAQ7PassageWithANumber(string figure)
    {
        GivenTheCommittedFeedbackAndItsPreFill();
        string[] lines = DocumentChecks.Lines(_context.Text);
        int q7 = Array.FindIndex(lines, static line => line.StartsWith("## Q7.", StringComparison.Ordinal));
        int q8 = Array.FindIndex(lines, static line => line.StartsWith("## Q8.", StringComparison.Ordinal));
        Assert.True(q7 >= 0 && q8 > q7, "The feedback file has no Q7 section.");
        string sentence = "Measured " + figure + " on Nebius.";
        string replaced = DocumentChecks.WithChangedBlockReplaced(
            _context.Text,
            _context.PreFill!,
            hunk => hunk.AfterStart > q7 && hunk.AfterStart < q8 && hunk.After.Any(static line => line.Contains("latency", StringComparison.OrdinalIgnoreCase)),
            sentence);

        // A file whose Q7 measurements are not a changed block yet gets the sentence at the end of the Q7 section, which is a block of inserted lines.
        _context.Text = replaced != _context.Text ? replaced : string.Join('\n', [.. lines[..q8], sentence, string.Empty, .. lines[q8..]]);
        Assert.Contains(sentence, _context.Text, StringComparison.Ordinal);
    }

    [When(@"^the owner markers are counted and the user lines compared$")]
    public void WhenTheMarkersAreCounted()
    {
        _context.Failures = DocumentChecks.FeedbackMarkers(_context.Text, _context.PreFill!);
    }

    [When(@"^the heading ""Feedback form, user fills"" is read$")]
    public void WhenTheChecklistHeadingIsRead() => _context.Failures = DocumentChecks.FeedbackChecklist(_context.Checklist);

    [When(@"^each is checked$")]
    public void WhenEachIsChecked()
    {
        _context.Failures = DocumentChecks.FeedbackPassages(_context.Text, _context.PreFill!, RepositoryFiles.Exists);
    }

    [When(@"^the Q4 baseline passage is checked$")]
    public void WhenTheBaselinePassageIsChecked() => _context.Failures = DocumentChecks.FeedbackBaseline(_context.Text, baselineRunExists: false);

    [When(@"^every feedback check runs on the committed files$")]
    public void WhenEveryCheckRuns()
    {
        _context.Failures = CheckAll(_context.Text, DevpostCommitted.Checklist(), _context.BuildLog);
    }

    [When(@"^the feedback check runs$")]
    public void WhenTheFeedbackCheckRuns()
    {
        string checklist = string.IsNullOrEmpty(_context.Checklist) ? DevpostCommitted.Checklist() : _context.Checklist;
        string log = _context.BuildLog;
        _context.Failures = CheckAll(_context.Text, checklist, log);
    }

    [Then(@"^""owner: builder"" occurs 0 times and ""owner: user"" occurs 7 times$")]
    public void ThenTheMarkersAreCounted()
    {
        Assert.Equal(0, DocumentChecks.CountOf(_context.Text, "owner: builder"));
        Assert.Equal(7, DocumentChecks.CountOf(_context.Text, "owner: user"));
    }

    [Then(@"^the seven user lines are byte-identical to the pre-fill file$")]
    public void ThenTheUserLinesAreUnchanged()
    {
        Assert.Empty(_context.Failures);
    }

    [Then(@"^it has seven bullets naming Q2, Q4, Q5, Q6, Q7, Q8 and Q9 and docs/devpost-feedback\.md$")]
    public void ThenTheChecklistHasSevenBullets()
    {
        Assert.Empty(_context.Failures);
        Assert.Equal(7, DocumentChecks.SectionAtAnyLevel(_context.Checklist, DocumentChecks.FeedbackChecklistHeading).Count(static line => line.StartsWith("- ", StringComparison.Ordinal)));
    }

    [Then(@"^each names an existing file and a label word or an explicit not-run statement$")]
    public void ThenEachPassageIsSourced()
    {
        Assert.Empty(_context.Failures);
    }

    [Then(@"^it reads ""No Qwen3 8B baseline run was made\.""$")]
    public void ThenItReadsTheBaselineSentence()
    {
        Assert.Empty(_context.Failures);
        Assert.Contains(DocumentChecks.NoBaselineSentence, _context.Text, StringComparison.Ordinal);

        // The check bites: a file with the sentence is wrong when a baseline run exists, and one without it is wrong when none does.
        Assert.NotEmpty(DocumentChecks.FeedbackBaseline(_context.Text, baselineRunExists: true));
        Assert.NotEmpty(DocumentChecks.FeedbackBaseline("No sentence here.", baselineRunExists: false));
    }

    [Then(@"^every one passes$")]
    public void ThenEveryOnePasses()
    {
        Assert.Empty(_context.Failures);
    }

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure)
    {
        Assert.True(_context.Failures.Any(entry => entry.Contains(failure, StringComparison.Ordinal)), "failures: " + string.Join("; ", _context.Failures));
    }

    [Then(@"^no failure names ""([^""]*)""$")]
    public void ThenNoFailureNames(string failure)
    {
        Assert.DoesNotContain(_context.Failures, entry => entry.Contains(failure, StringComparison.Ordinal));
    }
}