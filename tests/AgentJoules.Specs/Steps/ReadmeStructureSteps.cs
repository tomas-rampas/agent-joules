using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-130: the README pitch and architecture diagram (REQ-025). Scoped to the feature because sentences such as "the README structure check runs" belong to this story only. The Given steps read the committed
/// README and pitch through <see cref="RepositoryRoot"/> and the negative ones mutate the text in memory; the checks are the pure functions of <see cref="DocumentChecks"/>.
/// </summary>
[Binding]
[Scope(Feature = "AJ-130 README pitch and architecture diagram")]
public sealed class ReadmeStructureSteps(ReadmeStructureContext context)
{
    private const string ReadmePath = "README.md";

    private readonly ReadmeStructureContext _context = context;

    [Given(@"^the committed README\.md$")]
    public void GivenTheCommittedReadme() => _context.Readme = RepositoryRoot.ReadAllText(ReadmePath);

    [Given(@"^the committed README\.md and docs/devpost-pitch\.md and a completed cloudgpu run file$")]
    public void GivenTheCommittedFilesAndACloudGpuRun()
    {
        _context.Readme = RepositoryRoot.ReadAllText(ReadmePath);
        _context.Pitch = RepositoryRoot.ReadAllText("docs/devpost-pitch.md");
        string[] files = [.. Directory.GetFiles(RepositoryRoot.Resolve("results"), "*Z-cloudgpu.json")];
        _context.CloudGpuRan = DocumentChecks.CloudGpuRan(files, File.ReadAllText);
        Assert.True(_context.CloudGpuRan, "The repository holds no completed or partial cloudgpu run file.");
    }

    // The committed README is plural because a completed file exists, so the repository without a cloudgpu run is modelled in memory: an empty file list, line 3 singular, a build log that logs the change.
    [Given(@"^the committed README\.md and no completed or partial cloudgpu run file$")]
    public void GivenTheCommittedReadmeAndNoCloudGpuRun()
    {
        string[] lines = DocumentChecks.Lines(RepositoryRoot.ReadAllText(ReadmePath));
        lines[2] = DocumentChecks.SingularTagline;
        _context.Readme = string.Join('\n', lines);
        _context.Pitch = RepositoryRoot.ReadAllText("docs/devpost-pitch.md");
        _context.CloudGpuRan = DocumentChecks.CloudGpuRan([], File.ReadAllText);
        _context.BuildLog = "- AJ-130 no cloudgpu run file: the README tagline reads \"" + DocumentChecks.SingularTagline + "\".";
    }

    [Given(@"^a copy of the README with the mermaid fence line removed$")]
    public void GivenAReadmeWithoutTheFence() =>
        _context.Readme = string.Join('\n', DocumentChecks.Lines(RepositoryRoot.ReadAllText(ReadmePath)).Where(static line => line != DocumentChecks.MermaidFence));

    [Given(@"^a copy of the README whose diagram has no OpenTelemetry node$")]
    public void GivenAReadmeWithoutTheOpenTelemetryNode()
    {
        string[] lines = DocumentChecks.Lines(RepositoryRoot.ReadAllText(ReadmePath));
        int open = Array.IndexOf(lines, DocumentChecks.MermaidFence);
        int close = Array.FindIndex(lines, open + 1, static line => line == DocumentChecks.CloseFence);
        _context.Readme = string.Join('\n', lines.Where((line, index) => index <= open || index >= close || !line.Contains("opentelemetry", StringComparison.OrdinalIgnoreCase)));
    }

    [Given(@"^a copy of the README without the heading ""([^""]*)""$")]
    public void GivenAReadmeWithoutTheHeading(string heading) =>
        _context.Readme = string.Join('\n', DocumentChecks.Lines(RepositoryRoot.ReadAllText(ReadmePath)).Where(line => line != heading));

    [Given(@"^a copy of the README that still says ""([^""]*)"" under Architecture$")]
    public void GivenAReadmeWithLeftoverText(string text)
    {
        List<string> lines = [.. DocumentChecks.Lines(RepositoryRoot.ReadAllText(ReadmePath))];
        lines.Insert(lines.IndexOf("## Architecture") + 1, text);
        _context.Readme = string.Join('\n', lines);
    }

    [When(@"^the Mermaid block is extracted$")]
    public void WhenTheMermaidBlockIsExtracted() => _context.Block = DocumentChecks.MermaidBlock(_context.Readme);

    [When(@"^line 3 of the README is compared with the Tagline section$")]
    [When(@"^line 3 of the README is checked$")]
    public void WhenLineThreeIsChecked() => _context.Failures = DocumentChecks.Tagline(_context.Readme, _context.Pitch, _context.CloudGpuRan);

    [When(@"^the README structure check runs$")]
    public void WhenTheStructureCheckRuns() => _context.Failures = DocumentChecks.ReadmeStructure(_context.Readme);

    [Then(@"^exactly one block exists and its first line is ""([^""]*)""$")]
    public void ThenOneBlockExists(string firstLine)
    {
        MermaidBlockResult block = _context.Block ?? throw new InvalidOperationException("The block has not been extracted.");
        Assert.Null(block.Failure);
        Assert.Equal(firstLine, block.Lines[0]);
    }

    [Then(@"^it names agents, metered clients, providers, the NVML meter, the sink, results, the report and OpenTelemetry$")]
    public void ThenItNamesTheEightWords()
    {
        MermaidBlockResult block = _context.Block ?? throw new InvalidOperationException("The block has not been extracted.");
        Assert.Empty(DocumentChecks.DiagramNodes(block.Lines));
    }

    [Then(@"^both read ""([^""]*)""$")]
    public void ThenBothRead(string tagline)
    {
        Assert.Empty(_context.Failures);
        Assert.Equal(tagline, DocumentChecks.Lines(_context.Readme)[2]);
        Assert.Equal(tagline, DocumentChecks.PitchTagline(_context.Pitch));
    }

    [Then(@"^the text is (\d+) characters$")]
    public void ThenTheTextIsCharacters(int length) => Assert.Equal(length, DocumentChecks.Lines(_context.Readme)[2].Length);

    [Then(@"^it reads ""([^""]*)""$")]
    public void ThenItReads(string tagline)
    {
        Assert.Empty(_context.Failures);
        Assert.Equal(tagline, DocumentChecks.Lines(_context.Readme)[2]);
    }

    [Then(@"^the change is logged in docs/build-log\.md$")]
    public void ThenTheChangeIsLogged() => Assert.Null(DocumentChecks.SingularTaglineLogged(_context.BuildLog));

    [Then(@"^it fails with ""([^""]*)""$")]
    public void ThenItFailsWith(string failure) => Assert.Contains(failure, _context.Failures);
}