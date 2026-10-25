using System.Text.RegularExpressions;

using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-107: select and record the local model (REQ-018, REQ-020, EDGE-009). Scoped to the feature because sentences such as "I run" and "the exit code is 5" are bound by the classes of other features for their own
/// meanings. The four scenarios about the Ollama record follow the AJ-101 and AJ-103 pattern: the Given step evaluates <see cref="LocalModelRecordContext.Open"/>; when the gate is closed, the committed decision of
/// <c>docs/nim-local-attempt.md</c> is not <c>ollama</c> or the record is absent, the remaining steps write the reason and pass; otherwise they assert the committed <c>## local</c> section of
/// <c>docs/endpoints.md</c> and never perform the builder's action. The two failure scenarios (Ollama stopped, an unpulled tag) are not gated: they run the real <c>run --tier local --dry-run</c> command against a
/// loopback fake that stands where Ollama stands, and check the exit code and the fix command.
/// </summary>
[Binding]
[Scope(Feature = "Select and record the local model")]
public sealed partial class LocalModelRecordSteps(LocalModelRecordContext context, IReqnrollOutputHelper output)
{
    private readonly LocalModelRecordContext _context = context;
    private readonly IReqnrollOutputHelper _output = output;

    private string _tag = OllamaFixtures.StoryTag;

    private string Section => string.Join('\n', _context.LocalLines);

    // True when the step must assert; otherwise the reason is written to the scenario output.
    private bool Asserting()
    {
        if (_context.IsActive)
        {
            return true;
        }

        _output.WriteLine("passed without asserting: " + _context.Note);
        return false;
    }

    [Given(@"^Ollama is running and the Local:Runtime decision is ""([^""]*)""$")]
    public void GivenOllamaIsRunningAndTheDecisionIs(string runtime)
    {
        Assert.Equal("ollama", runtime);
        _context.Open();
    }

    [Given(@"^""ollama list"" shows a Qwen3 8B tag$")]
    public void GivenOllamaListShowsAQwenTag() => _context.Open();

    [Given(@"^""ollama ps"" after warm-up shows less than 100 percent GPU for the 30b tag$")]
    public void GivenOllamaPsShowsLessThan100Percent() => _context.Open();

    [Given(@"^the Ollama library page lists no smaller verified Nemotron tag$")]
    public void GivenTheLibraryListsNoSmallerTag() => _context.Open();

    [When(@"^the builder pulls and shows ""([^""]*)"" and lists the models endpoint$")]
    public void WhenTheBuilderPullsAndShows(string tag) => _tag = tag;

    [When(@"^the builder writes the ""## local"" section$")]
    public static void WhenTheBuilderWritesTheLocalSection()
    {
        // The builder's action is not performed here: the committed section is only read by the Then step.
    }

    [When(@"^the builder records the model choice$")]
    public static void WhenTheBuilderRecordsTheModelChoice()
    {
        // The builder's action is not performed here: the committed section is only read by the Then step.
    }

    [Then(@"^the ""## local"" section holds the tag, the quantization, the size and a Verified date$")]
    public void ThenTheSectionHoldsTheTagTheQuantizationTheSizeAndAVerifiedDate()
    {
        if (!Asserting())
        {
            return;
        }

        Assert.Contains(_tag, Section, StringComparison.Ordinal);
        Assert.Matches(@"(?im)\bquantization\b.*\b(Q\d\w*|F16|BF16|FP\d+)\b", Section);
        Assert.Matches(@"(?im)\bsize\b.*\d", Section);
        Assert.Matches(@"(?m)^Verified: \d{4}-\d{2}-\d{2}\b", Section);
    }

    [Then(@"^the Qwen tag is recorded exactly and marked ""baseline \(non-Nemotron\)""$")]
    public void ThenTheQwenTagIsRecordedAsBaseline()
    {
        if (!Asserting())
        {
            return;
        }

        Assert.Contains(_context.LocalLines, static line => QwenTagLine().IsMatch(line) && line.Contains("baseline (non-Nemotron)", StringComparison.Ordinal));
    }

    [Then(@"^the processor split and the reason for keeping or replacing the tag are recorded$")]
    public void ThenTheProcessorSplitAndTheReasonAreRecorded()
    {
        if (!Asserting())
        {
            return;
        }

        Assert.Matches(@"(?i)processor split|\d+ ?% ?GPU|\d+ percent GPU|offload", Section);
        Assert.Matches(@"(?i)\b(keep|kept|keeping|replace|replaced|replacing)\b", Section);
    }

    [Then(@"^the 30b tag is kept and the result of the library check is recorded$")]
    public void ThenTheTagIsKeptAndTheLibraryCheckIsRecorded()
    {
        if (!Asserting())
        {
            return;
        }

        Assert.Matches(@"(?i)\blibrary\b", Section);
        Assert.Matches(@"(?i)\b(keep|kept|keeping)\b.*30b|30b.*\b(keep|kept|keeping)\b", Section);
    }

    [Given(@"^the Ollama service is stopped$")]
    public void GivenTheOllamaServiceIsStopped()
    {
        FakeHttpMessageHandler stopped = OllamaFixtures.Endpoint(OllamaFixtures.StoryTag);
        stopped.ConnectionRefused = true;
        _context.Endpoint = stopped;
    }

    [Given(@"^the local model ID is overridden to a tag that is not pulled$")]
    public void GivenTheLocalModelIdIsOverriddenToAnUnpulledTag()
    {
        // Ollama is up and lists the story tag only; the override names another tag.
        _context.Endpoint = OllamaFixtures.Endpoint(OllamaFixtures.StoryTag);
        _context.ConfiguredTag = OllamaFixtures.OtherTag;
    }

    [When(@"^I run ""([^""]*)""$")]
    public async Task WhenIRun(string arguments) => await _context.RunAsync(arguments);

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        CliRunResult result = _context.Result ?? throw new Xunit.Sdk.XunitException("The command has not run yet.");
        Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");
        Assert.Empty(result.DeniedRequests);
        Assert.DoesNotContain(ProviderClientFixtures.FakeKey(), _context.Output, StringComparison.Ordinal);
    }

    [Then(@"^the output contains ""([^""]*)""$")]
    public void ThenTheOutputContains(string text) => Assert.Contains(text, _context.Output, StringComparison.Ordinal);

    [Then(@"^the output contains ""ollama pull"" followed by that tag$")]
    public void ThenTheOutputContainsThePullCommandOfThatTag()
    {
        string tag = _context.ConfiguredTag ?? throw new Xunit.Sdk.XunitException("No tag was overridden.");
        Assert.Contains("ollama pull " + tag, _context.Output, StringComparison.Ordinal);
    }

    // A line that records a Qwen3 tag exactly as ollama list prints it (name:tag).
    [GeneratedRegex(@"\bqwen3[\w.-]*:[\w.-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QwenTagLine();
}