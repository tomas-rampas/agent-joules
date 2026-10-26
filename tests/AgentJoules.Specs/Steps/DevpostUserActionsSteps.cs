using AgentJoules.Specs.Support;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-139: the user-action sections and commands of the Devpost checklist (REQ-028). Scoped to the feature because sentences such as "it fails with" and "the checklist check runs" belong to several documentation
/// stories. The Given steps read the committed checklist through <see cref="DevpostCommitted"/> and the negative ones mutate the text in memory; the checks are the pure functions of <see cref="DocumentChecks"/>.
/// </summary>
[Binding]
[Scope(Feature = "AJ-139 Devpost checklist user actions")]
public sealed class DevpostUserActionsSteps(DevpostContext context)
{
    private readonly DevpostContext _context = context;

    [Given(@"^the committed docs/devpost-checklist\.md$")]
    [Given(@"^the committed checklist$")]
    [Given(@"^the Judging criteria section$")]
    [Given(@"^the Tracks section$")]
    public void GivenTheCommittedChecklist()
    {
        _context.Text = DevpostCommitted.Checklist();
        _context.CloudGpuRan = DevpostCommitted.CloudGpuRan();
    }

    [Given(@"^a copy of the checklist with ""Devpost handle: ([^""]*)""$")]
    public void GivenACopyWithAFilledHandle(string handle)
    {
        GivenTheCommittedChecklist();
        string[] lines = DocumentChecks.Lines(_context.Text);
        int at = Array.FindIndex(lines, static line => line.TrimStart('-', ' ').StartsWith("Devpost handle:", StringComparison.Ordinal));
        Assert.True(at >= 0, "The checklist has no Devpost handle line.");
        lines[at] = "Devpost handle: " + handle;
        _context.Text = string.Join('\n', lines);
    }

    [Given(@"^a copy of the checklist without the string ""([^""]*)""$")]
    public void GivenACopyWithoutTheString(string text)
    {
        GivenTheCommittedChecklist();
        _context.Text = DocumentChecks.WithoutPhrase(_context.Text, text, "removed command");
        Assert.DoesNotContain(text, _context.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Given(@"^a copy of the checklist whose Pages block lacks the UI path$")]
    public void GivenACopyWithAPagesBlockWithoutTheUiPath()
    {
        GivenTheCommittedChecklist();
        string[] lines = DocumentChecks.Lines(_context.Text);
        int command = Array.FindIndex(lines, static line => line.Contains("gh api", StringComparison.Ordinal));
        Assert.True(command > 0, "The checklist has no Pages command.");
        int open = Array.FindLastIndex(lines, command, static line => line.StartsWith("```", StringComparison.Ordinal));
        int close = Array.FindIndex(lines, command, static line => line.StartsWith("```", StringComparison.Ordinal));
        Assert.True(open >= 0 && close > open, "The Pages command is not in a fenced block.");
        string[] kept = [.. lines.Where((line, index) => index <= open || index >= close || !line.Contains("Deploy from a branch", StringComparison.Ordinal))];

        // The phrase stays in the file as prose, so only the block rule can reject the copy.
        _context.Text = string.Join('\n', [.. kept, string.Empty, "In the Pages settings choose Deploy from a branch, branch main, folder /docs."]);
    }

    [Given(@"^a copy of the checklist where the push block lacks the user-action label$")]
    public void GivenACopyWithAnUnlabelledPushBlock()
    {
        GivenTheCommittedChecklist();
        string[] lines = DocumentChecks.Lines(_context.Text);
        int command = Array.FindIndex(lines, static line => line.Contains("git push -u origin main", StringComparison.Ordinal));
        Assert.True(command > 0, "The checklist has no push command.");
        int label = Array.FindLastIndex(lines, command, static line => line.Trim() == DocumentChecks.UserActionLabel);
        Assert.True(label >= 0, "The push block has no label.");
        lines[label] = "Run this yourself:";
        _context.Text = string.Join('\n', lines);
    }

    [Given(@"^a copy of the checklist without the heading ""([^""]*)""$")]
    public void GivenACopyWithoutTheHeading(string heading)
    {
        GivenTheCommittedChecklist();
        _context.Text = DocumentChecks.WithoutHeading(_context.Text, heading);
        Assert.NotNull(DocumentChecks.HasHeadingAtAnyLevel(_context.Text, heading));
    }

    [When(@"^the Registration section is read$")]
    public void WhenTheRegistrationSectionIsRead()
    {
        _context.Section = DocumentChecks.SectionAtAnyLevel(_context.Text, "Registration");
        _context.Failures = DocumentChecks.ChecklistRegistration(_context.Text);
    }

    [When(@"^the fenced blocks are extracted$")]
    public void WhenTheFencedBlocksAreExtracted()
    {
        _context.Blocks = DocumentChecks.LabelledBlocks(_context.Text);
        _context.Failures = DocumentChecks.ChecklistCommandBlocks(_context.Text);
    }

    [When(@"^each mapped artifact is checked$")]
    public void WhenEachMappedArtifactIsChecked() =>
        _context.Failures = DocumentChecks.ChecklistJudging(_context.Text, DevpostCommitted.Readme(), RepositoryFiles.Exists);

    [When(@"^it is read$")]
    public void WhenItIsRead() => _context.Failures = DocumentChecks.ChecklistTracks(_context.Text);

    [When(@"^the Credits section, the Pages label and the Before you push list are checked$")]
    public void WhenTheCreditsAndTheListsAreChecked() =>
        _context.Failures = [.. DocumentChecks.ChecklistCredits(_context.Text), .. DocumentChecks.ChecklistCommandBlocks(_context.Text), .. DocumentChecks.ChecklistBeforePush(_context.Text), .. DocumentChecks.ChecklistUserHeadings(_context.Text)];

    [When(@"^the checklist check runs$")]
    public void WhenTheChecklistCheckRuns() => _context.Failures = DevpostCommitted.ChecklistFailures(_context.Text, _context.CloudGpuRan);

    [Then(@"^it has the four exact lines and three values read ""<user fills>""$")]
    public void ThenItHasTheFourLines()
    {
        Assert.Empty(_context.Failures);
        string[] lines = [.. _context.Section.Select(static line => line.Trim().TrimStart('-').Trim())];
        foreach (string expected in new[] { "Devpost handle: <user fills>", "Joined on: <user fills>", "Team: solo unless stated", "Credits requested on: <user fills>" })
        {
            Assert.Single(lines, expected);
        }
    }

    [Then(@"^blocks contain ""git push"", ""gh release create"" and the Pages settings$")]
    public void ThenBlocksContainTheCommands()
    {
        Assert.Empty(_context.Failures);
        foreach (string command in new[] { "git push", "gh release create", "gh api" })
        {
            Assert.Contains(_context.Blocks, block => block.Lines.Any(line => line.Contains(command, StringComparison.Ordinal)));
        }

        Assert.Contains("Deploy from a branch", _context.Text, StringComparison.Ordinal);
    }

    [Then(@"^each is preceded by ""User action \(the builder never runs this\):""$")]
    public void ThenEachIsLabelled()
    {
        foreach (string command in new[] { "git push", "gh release create", "gh api" })
        {
            LabelledBlock block = _context.Blocks.First(entry => entry.Lines.Any(line => line.Contains(command, StringComparison.Ordinal)));
            Assert.Equal(DocumentChecks.UserActionLabel, block.Label);
        }
    }

    [Then(@"^the four criteria map to paths or README headings that exist$")]
    public void ThenTheCriteriaMap() => Assert.Empty(_context.Failures);

    [Then(@"^it recommends one track with one reason and says ""Final pick: the user decides""$")]
    public void ThenItRecommendsOneTrack()
    {
        Assert.Empty(_context.Failures);
        Assert.Contains(DocumentChecks.SectionAtAnyLevel(_context.Text, "Tracks"), static line => line.Trim() == "Final pick: the user decides");
    }

    [Then(@"^every one passes$")]
    public void ThenEveryOnePasses() => Assert.Empty(_context.Failures);

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure) =>
        Assert.True(_context.Failures.Any(entry => entry.Contains(failure, StringComparison.Ordinal)), "failures: " + string.Join("; ", _context.Failures));
}