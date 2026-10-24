using System.Text.Json.Nodes;

using AgentJoules.Cli.Output;
using AgentJoules.Core.Security;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// The steps of two configuration stories. AJ-010 (redact key values) binds the unscoped sentences at the top of the class. AJ-100 (<c>AJ validate --config</c> checks the configured model IDs against
/// <c>docs/endpoints.md</c>, REQ-018) binds the sentences at the end, each scoped to its feature because "the exit code is 0" and "I run ..." are sentences of every CLI story. The state of an AJ-100 scenario lives in
/// <see cref="EndpointsValidationContext"/>; every scenario runs the real command line through the harness in a temporary workspace that holds its own configuration and its own endpoints document.
/// </summary>
[Binding]
public sealed class ConfigurationSteps(EndpointsValidationContext endpoints) : IDisposable
{
    private const string KeyPlaceholder = "<key>";

    private SecretRedactor _redactor = new();
    private StringWriter? _inner;
    private RedactingTextWriter? _writer;
    private string _opaqueKey = string.Empty;
    private string _input = string.Empty;
    private string? _result;
    private string? _secondResult;
    private Exception? _thrown;

    public void Dispose()
    {
        _writer?.Dispose();
        _inner?.Dispose();
    }

    [Given(@"a redactor registered with a runtime-built opaque key")]
    public void GivenARedactorRegisteredWithARuntimeBuiltOpaqueKey()
    {
        _opaqueKey = Guid.NewGuid().ToString("N");
        _redactor = new SecretRedactor();
        _redactor.Register(_opaqueKey);
    }

    [Given(@"a redactor with no registered values")]
    public void GivenARedactorWithNoRegisteredValues()
    {
        _redactor = new SecretRedactor();
    }

    [Given(@"a redactor registered with null, empty and whitespace values")]
    public void GivenARedactorRegisteredWithNullEmptyAndWhitespaceValues()
    {
        _redactor = new SecretRedactor();
        _redactor.Register(null);
        _redactor.Register(string.Empty);
        _redactor.Register("   ");
    }

    [Given(@"a redactor registered with the 5-character value ""([^""]*)""")]
    public void GivenARedactorRegisteredWithTheFiveCharacterValue(string value)
    {
        Assert.Equal(5, value.Length);
        _redactor = new SecretRedactor();
        _redactor.Register(value);
    }

    [Given(@"a redactor")]
    public void GivenARedactor()
    {
        _redactor = new SecretRedactor();
    }

    [Given(@"the text ""([^""]*)""")]
    public void GivenTheText(string text)
    {
        _redactor = new SecretRedactor();
        _input = text;
    }

    [Given(@"a RedactingTextWriter over a StringWriter")]
    public void GivenARedactingTextWriterOverAStringWriter()
    {
        _redactor = new SecretRedactor();
        _inner = new StringWriter();
        _writer = new RedactingTextWriter(_inner, _redactor);
    }

    [When(@"I redact ""([^""]*)""")]
    public void WhenIRedactText(string text)
    {
        _input = text.Replace(KeyPlaceholder, _opaqueKey, StringComparison.Ordinal);
        _thrown = Record.Exception(() => _result = _redactor.Redact(_input));
    }

    [When(@"I redact a runtime-built ""([^""]*)"" plus 16 characters")]
    public void WhenIRedactARuntimeBuiltValuePlus16Characters(string prefix)
    {
        _input = string.Concat(prefix, "abcdef", "1234567890");
        Assert.Equal(prefix.Length + 16, _input.Length);
        _thrown = Record.Exception(() => _result = _redactor.Redact(_input));
    }

    [When(@"I redact null")]
    public void WhenIRedactNull()
    {
        _thrown = Record.Exception(() => _result = _redactor.Redact(null));
    }

    [When(@"I redact it twice")]
    public void WhenIRedactItTwice()
    {
        _thrown = Record.Exception(() =>
        {
            _result = _redactor.Redact(_input);
            _secondResult = _redactor.Redact(_result);
        });
    }

    [When(@"""([^""]*)"" and ""([^""]*)"" are written separately and then a newline")]
    public void WhenTwoPartsAreWrittenSeparatelyAndThenANewline(string first, string second)
    {
        Assert.NotNull(_writer);
        _writer.Write(first);
        _writer.Write(second);
        _writer.Write("\n");
    }

    [Then(@"both occurrences become ""([^""]*)"" and no character of the key remains")]
    public void ThenBothOccurrencesBecomeTheMaskAndNoCharacterOfTheKeyRemains(string mask)
    {
        string expected = _input.Replace(_opaqueKey, mask, StringComparison.Ordinal);
        Assert.Null(_thrown);
        Assert.Equal(expected, _result);
        Assert.Equal(2, CountOccurrences(_result, mask));
        for (int start = 0; start + 6 <= _opaqueKey.Length; start++)
        {
            Assert.DoesNotContain(_opaqueKey.Substring(start, 6), _result, StringComparison.Ordinal);
        }
    }

    [Then(@"the result is ""([^""]*)""")]
    public void ThenTheResultIs(string expected)
    {
        Assert.Null(_thrown);
        Assert.Equal(expected, _result);
    }

    [Then(@"the inner writer receives only ""([^""]*)"" followed by the newline")]
    public void ThenTheInnerWriterReceivesOnlyTheMaskedKeyFollowedByTheNewline(string expected)
    {
        Assert.NotNull(_inner);
        Assert.Equal(expected + "\n", _inner.ToString());
    }

    [Then(@"the text is unchanged and no exception is thrown")]
    public void ThenTheTextIsUnchangedAndNoExceptionIsThrown()
    {
        Assert.Null(_thrown);
        Assert.Equal(_input, _result);
    }

    [Then(@"the output is ""([^""]*)"" because every registered value is masked completely")]
    public void ThenTheOutputIsBecauseEveryRegisteredValueIsMaskedCompletely(string expected)
    {
        Assert.Null(_thrown);
        Assert.Equal(expected, _result);
    }

    [Then(@"the result is an empty string and no exception is thrown")]
    public void ThenTheResultIsAnEmptyStringAndNoExceptionIsThrown()
    {
        Assert.Null(_thrown);
        Assert.Equal(string.Empty, _result);
    }

    [Then(@"both results equal ""([^""]*)""")]
    public void ThenBothResultsEqual(string expected)
    {
        Assert.Null(_thrown);
        Assert.Equal(expected, _result);
        Assert.Equal(expected, _secondResult);
        Assert.Equal(_result, _redactor.Redact(_result));
    }

    // ---- AJ-100: validate --config (REQ-018) ----

    private const string EndpointsFeature = "Validate configured model IDs against docs/endpoints.md";

    // A command that hangs must fail the scenario, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly EndpointsValidationContext _endpoints = endpoints;

    [Given(@"^a docs/endpoints\.md with sections local, nvidia and nebius, each with a Verified date and its model lines$")]
    [Scope(Feature = EndpointsFeature)]
    public void GivenADocumentWithThreeVerifiedSections()
    {
        foreach (string tier in EndpointsFixtures.DefaultTiers)
        {
            EndpointsValidationContext.SectionDraft draft = _endpoints.Draft(tier);
            Assert.Equal(EndpointsFixtures.VerifiedDate, draft.Verified);
            Assert.Equal([EndpointsFixtures.ModelOf(tier)], draft.Ids);
        }
    }

    [Given(@"^a configuration whose tier and agent model IDs all appear in those sections$")]
    [Scope(Feature = EndpointsFeature)]
    public void GivenAConfigurationWhoseIdsAllAppear()
    {
        // One agent model ID beside the tier model IDs, listed in the section of its tier.
        const string agentModel = "model-nvidia-writer";
        EndpointsFixtures.TierOf(_endpoints.Config, "nvidia")["AgentModelIds"] = new JsonObject { ["Writer"] = agentModel };
        _endpoints.Draft("nvidia").Ids.Add(agentModel);
    }

    [Given(@"^Judge:Tier is ""([^""]*)"" and Judge:ModelId is listed only under ""## ([^""]*)""$")]
    [Scope(Feature = EndpointsFeature)]
    public void GivenTheJudgeIdIsListedOnlyUnderTheJudgeTier(string judgeTier, string heading)
    {
        Assert.Equal(judgeTier, heading);
        const string judgeModel = "judge-model-listed-only-under-the-judge-tier";
        _endpoints.Section["Judge"] = new JsonObject { ["Tier"] = judgeTier, ["ModelId"] = judgeModel };
        _endpoints.Draft(judgeTier).Ids.Add(judgeModel);
    }

    [Given(@"^Tiers:cloudgpu:Enabled is false and ""## cloudgpu"" has no Verified line$")]
    [Scope(Feature = EndpointsFeature)]
    public void GivenCloudGpuIsDisabledAndItsSectionHasNoVerifiedLine() => _endpoints.DisableCloudGpuWithoutVerifiedLine();

    [Given(@"^the configuration names a model ID that is not listed in its section$")]
    [Scope(Feature = EndpointsFeature)]
    public void GivenTheConfigurationNamesAnUnlistedModelId() => _endpoints.NameUnlistedModel("nebius", "model-nebius-not-listed");

    [Given(@"^the section lists ""([^""]*)"" and the configuration names ""([^""]*)""$")]
    [Scope(Feature = EndpointsFeature)]
    public void GivenTheSectionListsOneSpellingAndTheConfigurationNamesAnother(string listed, string named)
    {
        Assert.NotEqual(listed, named);
        Assert.Equal(listed, named, ignoreCase: true);
        EndpointsValidationContext.SectionDraft local = _endpoints.Draft("local");
        local.Ids.Clear();
        local.Ids.Add(listed);
        EndpointsFixtures.TierOf(_endpoints.Config, "local")["ModelId"] = named;
    }

    [Given(@"^the ""## ([^""]*)"" section has model lines but no Verified line$")]
    [Scope(Feature = EndpointsFeature)]
    public void GivenASectionHasModelLinesButNoVerifiedLine(string tier)
    {
        EndpointsValidationContext.SectionDraft draft = _endpoints.Draft(tier);
        Assert.NotEmpty(draft.Ids);
        draft.Verified = null;
    }

    [Given(@"^Judge:Tier is ""([^""]*)"" and the judge ID is listed only under ""## ([^""]*)""$")]
    [Scope(Feature = EndpointsFeature)]
    public void GivenTheJudgeIdIsListedOnlyUnderAnotherTier(string judgeTier, string listedUnder)
    {
        Assert.NotEqual(judgeTier, listedUnder);
        const string judgeModel = "judge-model-listed-only-under-another-tier";
        _endpoints.Section["Judge"] = new JsonObject { ["Tier"] = judgeTier, ["ModelId"] = judgeModel };
        _endpoints.Draft(listedUnder).Ids.Add(judgeModel);
    }

    [When(@"^I run ""([^""]*)""$")]
    [Scope(Feature = EndpointsFeature)]
    public async Task WhenIRun(string arguments) => await _endpoints.RunAsync(arguments).WaitAsync(HangGuard);

    [Then(@"^the exit code is (\d+)$")]
    [Scope(Feature = EndpointsFeature)]
    public void ThenTheExitCodeIs(int expected)
    {
        CliRunResult result = _endpoints.Required();

        Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");

        // An exit 2 must come from the command itself, never from the parse check.
        Assert.DoesNotContain("(ParseError)", result.Stderr, StringComparison.Ordinal);
    }

    [Then(@"^the output contains ""([^""]*)"" and ""([^""]*)""$")]
    [Scope(Feature = EndpointsFeature)]
    public void ThenTheOutputContainsBoth(string first, string second)
    {
        string line = Assert.Single(_endpoints.StdoutLines);

        Assert.Contains(first, line, StringComparison.Ordinal);
        Assert.Contains(second, line, StringComparison.Ordinal);
        Assert.Matches(@"^Validated \d+ model IDs$", line);
    }

    [Then(@"^the output contains ""([^""]*)""$")]
    [Scope(Feature = EndpointsFeature)]
    public void ThenTheOutputContains(string expected) => Assert.Contains(expected, _endpoints.StdoutLines);

    [Then(@"^the output names the missing ID and its section$")]
    [Scope(Feature = EndpointsFeature)]
    public void ThenTheOutputNamesTheMissingIdAndItsSection()
    {
        string expected = _endpoints.ExpectedMissingLine ?? throw new Xunit.Sdk.XunitException("The scenario made no model ID absent.");

        Assert.Equal([expected], _endpoints.StdoutLines);
    }

    [Then(@"^the fake HTTP handler recorded (\d+) requests$")]
    [Scope(Feature = EndpointsFeature)]
    public void ThenTheFakeHandlerRecordedRequests(int expected)
    {
        Assert.Equal(expected, _endpoints.Handler.Attempts);
        Assert.Equal(expected, _endpoints.Required().DeniedRequests.Count);
    }

    private static int CountOccurrences(string? text, string value)
    {
        int count = 0;
        int index = 0;
        while (text is not null && (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}