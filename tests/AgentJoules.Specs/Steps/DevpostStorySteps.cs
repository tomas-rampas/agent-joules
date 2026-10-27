using AgentJoules.Specs.Support;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-140: the Devpost story fill (REQ-028). Scoped to the feature because sentences such as "it fails with" and "the story check runs" belong to several documentation stories. The committed story, the checklist and the
/// build log are read through <see cref="DevpostCommitted"/>; the pre-fill story is read from git by <see cref="PreFillFiles"/> (a committed fixture, compared with the git object when git can read it). The negative scenarios mutate a copy in memory, and the cloudgpu-absent rule is modelled on a story built in memory, so no file is written.
/// </summary>
[Binding]
[Scope(Feature = "AJ-140 Devpost story fill")]
public sealed class DevpostStorySteps(DevpostContext context)
{
    private const string Edited = "Energy here means GPU-board energy, not CPU, system memory or wall power.";

    // A story of the cloudgpu-absent branch: the cloud passage says so with its reason and no other sentence names the instance.
    private const string AbsentStory =
        "## Accomplishments that I'm proud of\n\n"
        + "The cloudgpu tier did not run in this submission: no cloudgpu run file was committed, as docs/cloudgpu-run.md records in its Teardown section.\n\n"
        + "On the laptop the median net energy per run is 120.5 J (measured; results/20261025T131930Z-local.json).\n";

    private readonly DevpostContext _context = context;

    private static string[] ChecklistNamesTheStory(string checklist) =>
        DocumentChecks.Lines(checklist).Any(static line => line.StartsWith("- [", StringComparison.Ordinal) && line.Contains("About the project", StringComparison.OrdinalIgnoreCase) && line.Contains(DocumentChecks.StoryPath, StringComparison.Ordinal))
            ? []
            : ["the checklist does not name " + DocumentChecks.StoryPath + " for About the project"];

    [Given(@"^the committed docs/devpost-story\.md$")]
    [Given(@"^the nine filled passages$")]
    public void GivenTheCommittedStory()
    {
        _context.Text = DevpostCommitted.Story();
        _context.CloudGpuRan = DevpostCommitted.CloudGpuRan();
        _context.BuildLog = DevpostCommitted.BuildLog();
    }

    [Given(@"^the pre-fill story and the filled story$")]
    public void GivenThePreFillStory()
    {
        GivenTheCommittedStory();
        _context.PreFill = PreFillFiles.Story();
    }

    [Given(@"^a workspace with no completed or partial cloudgpu run file$")]
    public void GivenAWorkspaceWithoutACloudGpuFile()
    {
        _context.CloudGpuRan = DocumentChecks.CloudGpuRan([], static _ => string.Empty);
        _context.Text = AbsentStory;
        Assert.False(_context.CloudGpuRan);
    }

    [Given(@"^a copy of the story with one ""\[\[FILL: \.\.\. \]\]"" left$")]
    public void GivenACopyWithAPlaceholder()
    {
        GivenTheCommittedStory();
        _context.Text = _context.Text.Replace("[[FILL:", "[FILL:", StringComparison.Ordinal) + "\n[[FILL: a value | source: a file]]\n";
    }

    [Given(@"^a copy of the story where the local median is written as ""([^""]*)"" with no results file$")]
    public void GivenACopyWithAnUnsourcedMedian(string quantity)
    {
        GivenTheCommittedStory();
        string sentence = "The median net energy per run on the local tier is " + quantity + " (measured).";
        string[] lines = DocumentChecks.Lines(_context.Text);
        IReadOnlyList<string> section = DocumentChecks.SectionAtAnyLevel(_context.Text, "Accomplishments that I'm proud of");
        string? median = section.FirstOrDefault(static line => line.Contains("median", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("-cloudgpu.json", StringComparison.Ordinal) && !line.Contains("Nebius AI Cloud", StringComparison.Ordinal) && !line.StartsWith("The code is open source", StringComparison.Ordinal));
        _context.Text = median is null
            ? DocumentChecks.WithSectionBody(_context.Text, "Accomplishments that I'm proud of", [.. section, sentence])
            : string.Join('\n', lines.Select(line => line == median ? sentence : line));
        Assert.Contains(sentence, _context.Text, StringComparison.Ordinal);
    }

    [Given(@"^a copy of the story where the sentence ""([^""]*)"" is edited$")]
    public void GivenACopyWithAnEditedSentence(string sentence)
    {
        GivenThePreFillStory();
        Assert.Equal(Edited, sentence);
        Assert.Contains(sentence, _context.Text, StringComparison.Ordinal);
        _context.Text = _context.Text.Replace(sentence, sentence.Replace("wall power", "wall power or fan power", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain(sentence, _context.Text, StringComparison.Ordinal);
    }

    [Given(@"^a copy of the story that names an agent ""([^""]*)"" that the run file does not hold$")]
    public void GivenACopyWithAnotherAgent(string agent)
    {
        GivenTheCommittedStory();
        string line = DocumentChecks.StoryPassageLines(_context.Text)[0];
        _context.Text = _context.Text.Replace(line, line.Replace(" and Reviewer", ", Reviewer and " + agent, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains(agent, _context.Text, StringComparison.Ordinal);
    }

    [Given(@"^a copy of the story where passage (\d) holds one more sentence$")]
    public void GivenACopyWithAnExtraSentence(int number)
    {
        GivenTheCommittedStory();
        string line = DocumentChecks.StoryPassageLines(_context.Text)[number - 1];
        _context.Text = _context.Text.Replace(line, line + " It also states a second claim.", StringComparison.Ordinal);
    }

    [Given(@"^a copy of the story where passage 5 names no file in parentheses$")]
    public void GivenACopyWithoutAParenthesisedFile()
    {
        GivenTheCommittedStory();
        string line = DocumentChecks.StoryPassageLines(_context.Text)[4];
        string stripped = System.Text.RegularExpressions.Regex.Replace(line, @"\([^()]*(?:results|docs)/[^()]*\)", string.Empty, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        Assert.NotEqual(line, stripped);
        _context.Text = _context.Text.Replace(line, stripped, StringComparison.Ordinal);
    }

    [Given(@"^a build log whose only entry records only the unedited sentence$")]
    public void GivenABuildLogWithTheKeyOnly() =>
        _context.BuildLog = "- Story change: " + Edited + " -> unchanged\n";

    [Given(@"^a copy of the story that still has the heading ""([^""]*)""$")]
    public void GivenACopyWithTheChecklistHeading(string heading)
    {
        GivenTheCommittedStory();
        _context.Text = _context.Text.TrimEnd('\n') + "\n\n## " + heading + "\n\nThis section is not part of the submitted story.\n";
    }

    [Given(@"^a copy of the story with ""([^""]*)""$")]
    public void GivenACopyWithASentence(string phrase)
    {
        GivenTheCommittedStory();
        _context.Text = _context.Text.TrimEnd('\n') + "\n\nThe energy per run is " + phrase + ".\n";
    }

    [When(@"^placeholders and headings are counted$")]
    public void WhenPlaceholdersAreCounted() => _context.Failures = [.. DocumentChecks.LeftoverPlaceholders(_context.Text), .. DocumentChecks.StoryChecklistDeleted(_context.Text)];

    [When(@"^each is checked$")]
    public void WhenEachIsChecked() =>
        _context.Failures = [.. DocumentChecks.StorySources(_context.Text, RepositoryFiles.Exists), .. DocumentChecks.StoryCloud(_context.Text, _context.CloudGpuRan), .. DocumentChecks.StoryMergedTiers(_context.Text)];

    [When(@"^the lines outside the nine passages are diffed$")]
    public void WhenTheLinesAreDiffed() => _context.Failures = DocumentChecks.StoryChanges(_context.PreFill!, _context.Text, _context.BuildLog);

    [When(@"^the story is checked$")]
    public void WhenTheStoryIsChecked() => _context.Failures = DocumentChecks.StoryCloud(_context.Text, _context.CloudGpuRan);

    [When(@"^the ending, the comparison passage, the calibration passage and the checklist row are checked$")]
    public void WhenTheRulesAreChecked() =>
        _context.Failures =
        [
            .. DocumentChecks.StoryEndsWithParagraph(_context.Text),
            .. DocumentChecks.StoryComparison(_context.Text),
            .. DocumentChecks.StoryCalibration(_context.Text),
            .. ChecklistNamesTheStory(DevpostCommitted.Checklist()),
        ];

    [When(@"^the story check runs$")]
    public void WhenTheStoryCheckRuns() => _context.Failures = DocumentChecks.StoryChecks(_context.Text, RepositoryFiles.Exists, _context.CloudGpuRan);

    [When(@"^the diff check runs against that build log$")]
    public void WhenTheDiffCheckRunsAgainstTheLog() => _context.Failures = DocumentChecks.StoryChanges(_context.PreFill!, _context.Text, _context.BuildLog);

    [When(@"^the agent names are compared with the latest local run file$")]
    public void WhenTheAgentsAreCompared() => _context.Failures = DocumentChecks.StoryAgents(_context.Text, DevpostCommitted.LatestLocalAgents());

    [When(@"^the sentence counts and the passage 5 file are checked$")]
    public void WhenTheSentenceCountsAreChecked() => _context.Failures = DocumentChecks.StorySentenceCounts(_context.Text);

    [When(@"^the diff check runs without a build-log entry$")]
    public void WhenTheDiffCheckRunsWithoutAnEntry() => _context.Failures = DocumentChecks.StoryChanges(_context.PreFill!, _context.Text, string.Empty);

    [Then(@"^""\[\[FILL:"" occurs 0 times and no heading contains ""Fill-after-build""$")]
    public void ThenNoPlaceholderIsLeft()
    {
        Assert.Empty(_context.Failures);
        Assert.Equal(0, DocumentChecks.CountOf(_context.Text, "[[FILL:"));
        Assert.DoesNotContain(DocumentChecks.Lines(_context.Text), static line => line.StartsWith('#') && line.Contains("Fill-after-build", StringComparison.OrdinalIgnoreCase));
    }

    [Then(@"^each names an existing results file or document, a unit and one label word$")]
    public void ThenEachPassageIsSourced()
    {
        Assert.Empty(_context.Failures);
        Assert.True(DocumentChecks.StoryPassageLines(_context.Text).Count >= 9, "fewer than nine filled passages found");
    }

    [Then(@"^every difference has a ""Story change:"" entry in docs/build-log\.md$")]
    public void ThenEveryDifferenceIsLogged()
    {
        Assert.Empty(_context.Failures);

        // The fill changed the file: a story equal to its pre-fill version still holds every placeholder.
        Assert.NotEmpty(DocumentChecks.LineHunks(_context.PreFill!, _context.Text));
    }

    [Then(@"^the cloud passage says ""The cloudgpu tier did not run in this submission""$")]
    public void ThenTheCloudPassageSaysItDidNotRun()
    {
        Assert.Contains(DocumentChecks.NoCloudStorySentence, _context.Text, StringComparison.Ordinal);
        Assert.Empty(_context.Failures);
    }

    [Then(@"^no sentence claims a measured Nebius AI Cloud or L40S figure$")]
    public void ThenNoSentenceClaimsAFigure()
    {
        Assert.DoesNotContain("Nebius AI Cloud", _context.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("L40S", _context.Text, StringComparison.Ordinal);

        // The check bites: a measured figure for the instance after the sentence is rejected.
        string claim = _context.Text + "\nOn the Nebius AI Cloud instance the median is 929.87 J (measured).\n";
        Assert.Contains(DocumentChecks.StoryCloud(claim, false), static failure => failure.StartsWith("cloud claim without cloudgpu run", StringComparison.Ordinal));
        Assert.Contains(DocumentChecks.StoryCloud("A story without the sentence.", false), static failure => failure.StartsWith("missing sentence", StringComparison.Ordinal));
    }

    [Then(@"^every one passes$")]
    public void ThenEveryOnePasses() => Assert.Empty(_context.Failures);

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure)
    {
        Assert.True(_context.Failures.Any(entry => entry.Contains(failure, StringComparison.Ordinal)), "failures: " + string.Join("; ", _context.Failures));
    }
}