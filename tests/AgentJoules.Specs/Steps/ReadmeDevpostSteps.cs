using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-132: the README sections the Devpost form requires (REQ-025). Scoped to the feature because sentences such as "it fails with" and "the README structure check runs" belong to this story only. The Given steps read the
/// committed README, endpoints document and results through <see cref="RepositoryRoot"/> and the negative ones mutate the text in memory; the checks are the pure functions of <see cref="DocumentChecks"/>.
/// </summary>
[Binding]
[Scope(Feature = "AJ-132 README sections required by the Devpost form")]
public sealed class ReadmeDevpostSteps(ReadmeDevpostContext context)
{
    private const string ReadmePath = "README.md";

    private readonly ReadmeDevpostContext _context = context;

    private static string Committed() => RepositoryRoot.ReadAllText(ReadmePath);

    [Given(@"^the committed README\.md and the latest local results file$")]
    public void GivenTheCommittedReadmeAndTheLocalFile()
    {
        _context.Readme = Committed();
        _context.ReadLatestLocalFile();
    }

    [Given(@"^every model ID named in README\.md$")]
    public void GivenEveryModelIdNamed()
    {
        _context.Readme = Committed();
        _context.NamedIds = DocumentChecks.ModelIdsNamed(_context.Readme);
        Assert.NotEmpty(_context.NamedIds);
    }

    // The committed README may carry a cloudgpu section because a run exists, so the repository without a run is modelled in memory: the section is the fixed sentence and the workspace holds no cloudgpu file.
    [Given(@"^a workspace with no completed cloudgpu run file$")]
    public void GivenAWorkspaceWithoutACloudGpuRun()
    {
        string readme = DocumentChecks.WithSectionBody(Committed(), DocumentChecks.CloudSectionHeading, [DocumentChecks.NoCloudSentence]);
        _context.PrepareWorkspaceWithoutCloudGpu(readme);
        Assert.False(_context.CloudGpuRan);
    }

    [Given(@"^a copy of the README without ""([^""]*)""$")]
    public void GivenAReadmeWithoutTheHeading(string heading) => _context.Readme = DocumentChecks.WithoutHeading(Committed(), heading);

    [Given(@"^a copy of the README naming the model ID ""([^""]*)""$")]
    public void GivenAReadmeNamingAModelId(string id) => _context.Readme = Committed() + "\n\nThe model ID `" + id + "` is named here.\n";

    [Given(@"^a copy of the README naming the plain-text model tag ""([^""]*)""$")]
    public void GivenAReadmeNamingAPlainModelTag(string tag) => _context.Readme = Committed() + "\n\nThe local model is " + tag + " in this paragraph.\n";

    [Given(@"^a copy of the README whose cloud section states 9\.999 GPU-hours$")]
    public void GivenAReadmeWithAWrongGpuHoursFigure()
    {
        string text = Committed();
        IReadOnlyList<string> body = DocumentChecks.SectionAtAnyLevel(text, DocumentChecks.CloudSectionHeading);
        Assert.Contains(body, static line => line.Contains("1.509 GPU-hours", StringComparison.Ordinal));
        _context.Readme = DocumentChecks.WithSectionBody(text, DocumentChecks.CloudSectionHeading, [.. body.Select(static line => line.Replace("1.509 GPU-hours", "9.999 GPU-hours", StringComparison.Ordinal))]);
    }

    [Given(@"^a copy of the README whose self-judging disclosure does not name the self-judged results file$")]
    public void GivenAReadmeWithoutTheSelfJudgedFileName()
    {
        _context.ReadLatestLocalFile();
        Assert.NotEmpty(_context.SelfJudgedFiles);
        IEnumerable<string> lines = DocumentChecks.Lines(Committed()).Select(line =>
            line.Contains("self-judg", StringComparison.OrdinalIgnoreCase) ? _context.SelfJudgedFiles.Aggregate(line, static (text, file) => text.Replace(file, "removed.json", StringComparison.Ordinal)) : line);
        _context.Readme = string.Join('\n', lines);
    }

    [Given(@"^a copy of the README stating an observed cost with no ""([^""]*)"" reference$")]
    public void GivenAReadmeWithACostAndNoSource(string source)
    {
        string text = DocumentChecks.WithSectionBody(Committed(), DocumentChecks.CloudSectionHeading, ["The observed cost of the VM was USD 7.12 for the run."]);
        Assert.DoesNotContain(source, string.Join('\n', DocumentChecks.SectionAtAnyLevel(text, DocumentChecks.CloudSectionHeading)), StringComparison.Ordinal);
        _context.Readme = text;
    }

    [Given(@"^a copy of the README that still contains ""([^""]*)""$")]
    public void GivenAReadmeWithLeftoverText(string text) => _context.Readme = Committed() + "\n" + text + "\n";

    [When(@"^the Devpost sections are extracted$")]
    public void WhenTheDevpostSectionsAreExtracted() => _context.Failures = DocumentChecks.DevpostStructure(_context.Readme);

    [When(@"^they are compared with the ""- model:"" lines of docs/endpoints\.md$")]
    public void WhenTheIdsAreCompared() => _context.Failures = DocumentChecks.ModelIds(_context.Readme, RepositoryRoot.ReadAllText("docs/endpoints.md"));

    [When(@"^the README ""Other Nebius tools or services used"" section is checked$")]
    public async Task WhenTheCloudSectionIsChecked()
    {
        Assert.Equal(0, await _context.UpdateWorkspaceReadmeAsync());
        _context.Failures = DocumentChecks.CloudSection(_context.Readme, _context.CloudGpuRan);
    }

    [When(@"^the README ""Other Nebius tools or services used"" section is checked against the cloudgpu run file$")]
    public void WhenTheCloudSectionIsCheckedAgainstTheRun()
    {
        bool ran = ReadmeDevpostContext.CommittedCloudGpuRan();
        Assert.True(ran, "The repository holds no completed or partial cloudgpu run file.");
        _context.Failures =
        [
            .. DocumentChecks.CloudSection(_context.Readme, ran),
            .. DocumentChecks.CloudFacts(_context.Readme, RepositoryRoot.ReadAllText(DocumentChecks.CloudGpuRunDocument)),
            .. DocumentChecks.CloudFigures(_context.Readme, RepositoryRoot.ReadAllText(DocumentChecks.CloudGpuRunDocument)),
            .. DocumentChecks.SourceLines(_context.Readme),
        ];
    }

    [When(@"^the cloud figures check runs$")]
    public void WhenTheCloudFiguresCheckRuns() =>
        _context.Failures = DocumentChecks.CloudFigures(_context.Readme, RepositoryRoot.ReadAllText(DocumentChecks.CloudGpuRunDocument));

    [When(@"^the README structure check runs$")]
    public void WhenTheStructureCheckRuns() => _context.Failures = DocumentChecks.DevpostStructure(_context.Readme);

    [When(@"^the model-ID check runs$")]
    public void WhenTheModelIdCheckRuns() => _context.Failures = DocumentChecks.ModelIds(_context.Readme, RepositoryRoot.ReadAllText("docs/endpoints.md"));

    [When(@"^the source-line check runs$")]
    public void WhenTheSourceLineCheckRuns() => _context.Failures = DocumentChecks.SourceLines(_context.Readme);

    [When(@"^the results files named in the README are looked up$")]
    public void WhenTheResultsFilesAreLookedUp() =>
        _context.Failures = DocumentChecks.ResultsFilesExist(_context.Readme, name => File.Exists(RepositoryRoot.Resolve("results/" + name)));

    [When(@"^the methodology summary is checked$")]
    public void WhenTheMethodologySummaryIsChecked() =>
        _context.Failures = DocumentChecks.MethodologySummary(_context.Readme, ReadmeDevpostContext.JudgeModelId(), _context.SelfJudgedFiles);

    [When(@"^the NIM-local outcome is checked$")]
    public void WhenTheNimLocalOutcomeIsChecked() => _context.Failures = DocumentChecks.NimLocalOutcome(_context.Readme);

    [Then(@"^the four required headings occur once each$")]
    public void ThenTheHeadingsOccurOnce() =>
        Assert.All(DocumentChecks.DevpostHeadings, heading => Assert.Null(DocumentChecks.HasHeadingAtAnyLevel(_context.Readme, heading)));

    [Then(@"^the sentence ""([^""]*)"" names the runtime and tag of the results file$")]
    public void ThenTheSentenceNamesTheRuntimeAndTag(string start)
    {
        Assert.Contains(start, _context.Readme, StringComparison.Ordinal);
        Assert.Empty(DocumentChecks.LocalRuntimeSentence(_context.Readme, _context.LocalRuntime, [.. _context.LocalModelIds]));
    }

    [Then(@"^each ID is listed and ""validate --config"" exits 0$")]
    public async Task ThenEachIdIsListedAndValidateExitsZero()
    {
        Assert.Empty(_context.Failures);
        await _context.ValidateConfigAsync();
        CliRunResult result = _context.ValidateResult ?? throw new InvalidOperationException("validate --config did not run.");

        Assert.True(result.ExitCode == 0, $"validate --config exited {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");
        Assert.Empty(result.DeniedRequests);
    }

    [Then(@"^it reads ""([^""]*)"" and the README labels the cloudgpu tier ""([^""]*)""$")]
    public void ThenItReadsTheFixedSentence(string sentence, string label)
    {
        Assert.Empty(_context.Failures);
        Assert.Equal([sentence], DocumentChecks.SectionAtAnyLevel(_context.Readme, DocumentChecks.CloudSectionHeading).Where(static line => line.Trim().Length > 0).Select(static line => line.Trim()));
        Assert.Contains("cloudgpu | " + label, _context.Readme, StringComparison.Ordinal);
    }

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure) => Assert.Contains(failure, _context.Failures);

    [Then(@"^it fails for each self-judged results file with the self-judging disclosure failure$")]
    public void ThenItFailsForEachSelfJudgedFile() =>
        Assert.Equal(_context.SelfJudgedFiles.Select(static file => "methodology summary lacks: self-judging disclosure naming " + file), _context.Failures);

    [Then(@"^the check passes$")]
    [Then(@"^each exists under results$")]
    [Then(@"^the section names docs/cloudgpu-run\.md, the preset, the region and the engine of that file$")]
    [Then(@"^it links docs/methodology\.md, names the judge model, excludes judge calls from the totals and discloses self-judging when the latest file says so$")]
    [Then(@"^the heading ""NIM-local outcome"" occurs once and docs/nim-local-attempt\.md is linked$")]
    public void ThenNoCheckFailed() => Assert.Empty(_context.Failures);
}