using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Readme;
using AgentJoules.Reporting.Render;
using AgentJoules.TestSupport;
using AgentJoules.TestSupport.Docs;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-129 (REQ-031, EDGE-017): the report, the README block and the Devpost tagline follow the real state of the cloudgpu tier. The scenarios run on scratch fixtures (no committed cloudgpu file exists): run documents built in
/// memory, the tagline rule of <see cref="CloudGpuTaglineRule"/> and the story check on scratch texts. Every step is scoped to the feature because sentences such as "the README block is rendered" belong to other stories.
/// </summary>
public sealed partial class CloudGpuSteps
{
    private const string ConsequencesFeature = "Apply the partial-tier consequences to the report, README and Devpost texts";
    private const string OtherGpu = "NVIDIA H100 80GB HBM3";

    private RunDocument? _consequencesCloud;
    private CloudGpuTierState? _consequencesState;
    private string? _consequencesTagline;
    private string? _consequencesBlock;
    private string? _consequencesSummary;
    private string? _consequencesStory;
    private string? _consequencesLog;
    private IReadOnlyList<string>? _consequencesProblems;

    private CloudGpuTierState ConsequencesState => _consequencesState ?? throw new Xunit.Sdk.XunitException("The tagline rule has not been evaluated yet.");

    private static RunStatus ConsequencesStatus(string status) =>
        status switch
        {
            "completed" => RunStatus.Completed,
            "partial" => RunStatus.Partial,
            "incomplete" => RunStatus.Incomplete,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "The feature uses completed, partial and incomplete."),
        };

    private void ConsequencesHaveAFile(string status, string gpuName, bool sampling = false) =>
        _consequencesCloud = CloudGpuConsequenceFixtures.Document(ConsequencesStatus(status), gpuName, sampling);

    private string ConsequencesRenderBlock()
    {
        List<SelectedRun> runs = [ReportRunFixtures.Wrap(RunDocumentFixtures.Committed(Tier.Local)), ReportRunFixtures.Wrap(RunDocumentFixtures.Committed(Tier.Nvidia))];
        if (_consequencesCloud is not null)
        {
            runs.Add(ReportRunFixtures.Wrap(_consequencesCloud));
        }

        return ReadmeResultsBlock.Render(ReportAggregator.Build(runs), dashboardScreenshotExists: false);
    }

    private string ConsequencesCloudRow() =>
        Assert.Single((_consequencesBlock ?? throw new Xunit.Sdk.XunitException("The README block has not been rendered yet.")).Split('\n'), static line => line.StartsWith("| cloudgpu |", StringComparison.Ordinal));

    [Given(@"^a committed cloudgpu run file with status ""([^""]*)"" and gpuName containing ""([^""]*)""$")]
    [Scope(Feature = ConsequencesFeature)]
    public void GivenACommittedFileWithStatusAndGpuNameConsequences(string status, string token) => ConsequencesHaveAFile(status, "NVIDIA " + token);

    [Given(@"^a committed cloudgpu run file with status ""([^""]*)""$")]
    [Scope(Feature = ConsequencesFeature)]
    public void GivenACommittedFileWithStatusConsequences(string status) => ConsequencesHaveAFile(status, CloudGpuConsequenceFixtures.L40S);

    [Given(@"^the committed cloudgpu file names a GPU other than the L40S$")]
    [Scope(Feature = ConsequencesFeature)]
    public void GivenTheFileNamesAnotherGpuConsequences() => ConsequencesHaveAFile("completed", OtherGpu);

    [Given(@"^no cloudgpu file with status ""completed"" or ""partial"" is committed$")]
    [Scope(Feature = ConsequencesFeature)]
    public void GivenNoUsableFileConsequences() => _consequencesCloud = null;

    [Given(@"^the only cloudgpu file has status ""([^""]*)""$")]
    [Scope(Feature = ConsequencesFeature)]
    public void GivenTheOnlyFileHasStatusConsequences(string status) => ConsequencesHaveAFile(status, CloudGpuConsequenceFixtures.L40S);

    [Given(@"^docs/devpost-story\.md differs from its pre-drafted sentence and docs/build-log\.md has no matching entry$")]
    [Scope(Feature = ConsequencesFeature)]
    public void GivenTheStoryDiffersWithoutAnEntryConsequences()
    {
        _consequencesStory = CloudGpuConsequenceFixtures.PreDraftedStory.Replace("an H100", "an L40S", StringComparison.Ordinal);
        _consequencesLog = "- AJ-128: the cloudgpu teardown is unconfirmed\n";
    }

    [Given(@"^a report fixture with a laptop local file and a cloudgpu file$")]
    [Scope(Feature = ConsequencesFeature)]
    public void GivenALaptopFileAndACloudFileConsequences() => ConsequencesHaveAFile("completed", CloudGpuConsequenceFixtures.L40S, sampling: true);

    [Given(@"^a completed cloudgpu file with method ""sampling""$")]
    [Scope(Feature = ConsequencesFeature)]
    public void GivenACompletedSamplingFileConsequences() => ConsequencesHaveAFile("completed", CloudGpuConsequenceFixtures.L40S, sampling: true);

    [When(@"^the tagline rule is evaluated$")]
    [Scope(Feature = ConsequencesFeature)]
    public void WhenTheTaglineRuleIsEvaluatedConsequences()
    {
        string[] files = _consequencesCloud is null ? [] : [CloudGpuConsequenceFixtures.FileName];
        _consequencesState = CloudGpuTaglineRule.Evaluate(files, _ => RunDocumentFixtures.Serialise(_consequencesCloud!) + "\n");
        _consequencesTagline = CloudGpuTaglineRule.Tagline(_consequencesState, CloudGpuConsequenceFixtures.CandidateA, CloudGpuConsequenceFixtures.CandidateALocal);
    }

    [When(@"^the README block is rendered$")]
    [Scope(Feature = ConsequencesFeature)]
    public void WhenTheReadmeBlockIsRenderedConsequences()
    {
        _consequencesBlock = ConsequencesRenderBlock();
        WhenTheTaglineRuleIsEvaluatedConsequences();
    }

    [When(@"^the consequence check runs$")]
    [Scope(Feature = ConsequencesFeature)]
    public void WhenTheConsequenceCheckRunsConsequences() =>
        _consequencesProblems = CloudGpuTaglineRule.ConsequenceProblems(_consequencesStory!, CloudGpuConsequenceFixtures.PreDraftedStory, _consequencesLog!);

    [When(@"^the report is rendered$")]
    [Scope(Feature = ConsequencesFeature)]
    public void WhenTheReportIsRenderedConsequences() =>
        _consequencesSummary = SummaryMarkdownRenderer.Render(ReportAggregator.Build([ReportRunFixtures.Wrap(RunDocumentFixtures.Committed(Tier.Local)), ReportRunFixtures.Wrap(_consequencesCloud!)]));

    [Then(@"^the tagline is candidate A and no change is required$")]
    [Scope(Feature = ConsequencesFeature)]
    public void ThenTheTaglineIsCandidateAUnchangedConsequences()
    {
        Assert.Equal("complete", ConsequencesState.State);
        Assert.Equal(CloudGpuConsequenceFixtures.CandidateA, _consequencesTagline);
    }

    [Then(@"^the cloudgpu row contains ""([^""]*)""$")]
    [Scope(Feature = ConsequencesFeature)]
    public void ThenTheCloudgpuRowContainsConsequences(string word) => Assert.Contains(word, ConsequencesCloudRow(), StringComparison.Ordinal);

    [Then(@"^the tagline rule keeps candidate A$")]
    [Scope(Feature = ConsequencesFeature)]
    public void ThenTheTaglineRuleKeepsCandidateAConsequences()
    {
        Assert.Equal("partial", ConsequencesState.State);
        Assert.Equal(CloudGpuConsequenceFixtures.CandidateA, _consequencesTagline);
    }

    [Then(@"^candidate A uses the file's gpuName$")]
    [Scope(Feature = ConsequencesFeature)]
    public void ThenCandidateAUsesTheGpuNameConsequences()
    {
        Assert.Equal(CloudGpuConsequenceFixtures.CandidateA.Replace("L40S", OtherGpu, StringComparison.Ordinal), _consequencesTagline);
        Assert.DoesNotContain("L40S", _consequencesTagline, StringComparison.Ordinal);
    }

    [Then(@"^docs/build-log\.md contains an entry for the change$")]
    [Scope(Feature = ConsequencesFeature)]
    public void ThenTheBuildLogHoldsAnEntryConsequences()
    {
        // The tagline of the scenario replaced the GPU token of candidate A: the change needs a log line with the task and the start of the old candidate (which A-local shares) and the new GPU name or the file that proves the new one.
        string tagline = _consequencesTagline ?? throw new Xunit.Sdk.XunitException("The tagline rule has not been evaluated yet.");
        string oldCandidate = CloudGpuConsequenceFixtures.CandidateA;
        string entry = "- AJ-129: tagline changed. Old: \"" + oldCandidate[..40] + "\" New GPU: " + OtherGpu + " Proof: results/" + CloudGpuConsequenceFixtures.FileName + "\n";

        Assert.NotEqual(oldCandidate, tagline);
        Assert.NotEmpty(CloudGpuTaglineRule.TaglineChangeProblems(oldCandidate, tagline, string.Empty));
        Assert.NotEmpty(CloudGpuTaglineRule.TaglineChangeProblems(oldCandidate, tagline, "- AJ-129: tagline changed. Old: \"" + oldCandidate[..40] + "\"\n"));
        Assert.Empty(CloudGpuTaglineRule.TaglineChangeProblems(oldCandidate, tagline, entry));
    }

    [Then(@"^the tagline is candidate A-local$")]
    [Scope(Feature = ConsequencesFeature)]
    public void ThenTheTaglineIsCandidateALocalConsequences()
    {
        Assert.Equal("absent", ConsequencesState.State);
        Assert.Equal(CloudGpuConsequenceFixtures.CandidateALocal, _consequencesTagline);
    }

    [Then(@"^the README block renders ""cloudgpu \| not run""$")]
    [Scope(Feature = ConsequencesFeature)]
    public void ThenTheBlockRendersNotRunConsequences() => Assert.Contains("cloudgpu | not run", _consequencesBlock ??= ConsequencesRenderBlock(), StringComparison.Ordinal);

    [Then(@"^the tier state is absent and candidate A-local is selected$")]
    [Scope(Feature = ConsequencesFeature)]
    public void ThenTheStateIsAbsentConsequences()
    {
        Assert.Equal(new CloudGpuTierState("absent", null, null, null), ConsequencesState);
        Assert.Equal(CloudGpuConsequenceFixtures.CandidateALocal, _consequencesTagline);
    }

    [Then(@"^the check fails and names the sentence$")]
    [Scope(Feature = ConsequencesFeature)]
    public void ThenTheCheckFailsAndNamesTheSentenceConsequences()
    {
        string problem = Assert.Single(_consequencesProblems!);
        Assert.Contains("\"The cloud tier runs on an H100.\"", problem, StringComparison.Ordinal);
    }

    [Then(@"^the summary has separate rows and contains no merged or averaged figure$")]
    [Scope(Feature = ConsequencesFeature)]
    public void ThenTheSummaryHasSeparateRowsConsequences()
    {
        string[] lines = _consequencesSummary!.Split('\n');
        string[] rows = [.. lines.Where(static line => line.StartsWith('|'))];
        Assert.Contains(rows, static row => row.StartsWith("| local |", StringComparison.Ordinal));
        Assert.Contains(rows, static row => row.StartsWith("| cloudgpu |", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, static row => row.StartsWith("| local |", StringComparison.Ordinal) && row.Contains("cloudgpu", StringComparison.Ordinal));
        Assert.Empty(CloudGpuConsequenceFixtures.MergedJouleLines(lines));
        Assert.DoesNotContain("average", _consequencesSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mean across tiers", _consequencesSummary, StringComparison.OrdinalIgnoreCase);
    }
}