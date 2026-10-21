using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-053: exit 4 before any completion call when a model ID is not listed. Scoped to the feature because sentences such as "the exit code is 4" are reused by other stories with
/// other meanings. The state lives in <see cref="ModelCheckContext"/>. Every scenario runs the stand-in <c>run</c> command of <see cref="RunCommandStandIn"/> through the real command
/// line harness, so the model check, the provider pipeline and the redacting writers are the real ones and the fake models route records what was asked.
/// </summary>
[Binding]
[Scope(Feature = "AJ-053 Exit 4 before any completion call when a model ID is not listed")]
public sealed class ModelCheckSteps(ModelCheckContext context)
{
    // The lines the failure prints for the list of available IDs are indented by two spaces under the header line.
    private const string IdIndent = "  ";

    private const string AbsentWriterModel = "acme/writer-model-not-listed";

    // A command that hangs must fail the scenario, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private readonly ModelCheckContext _context = context;

    private CliRunResult Result => _context.Result ?? throw new XunitException("The command has not run yet.");

    private IReadOnlyList<string> IdLines =>
        [.. Result.StderrLines.Where(static line => line.StartsWith(IdIndent, StringComparison.Ordinal)).Select(static line => line.Trim())];

    [Given(@"^a fake models response listing every configured model ID for tier ""([^""]*)""$")]
    public void GivenAFakeModelsResponseListingEveryConfiguredId(string tier)
    {
        Assert.Equal("nvidia", tier);

        // The tier model and the judge model of the shipped configuration (the same ID), and another entry so the list is not only what was configured.
        _context.Script(tier, [ModelCheckContext.ShippedModelId(tier), ModelCheckContext.ShippedJudgeModelId(), "acme/other-model"]);
    }

    [Given(@"^a fake models response with (\d+) IDs including (\d+) Nemotron and (\d+) Qwen entries$")]
    public void GivenAFakeModelsResponseWithManyIds(int total, int nemotron, int qwen)
    {
        // The Nemotron and Qwen entries sit late in the order of the provider, so a list that is only cut at 20 would lose some and a list that is not reordered would show them last.
        List<string> ids = [.. Enumerable.Range(1, total - nemotron - qwen).Select(static index => $"acme/model-{index:00}")];
        ids.Insert(9, "nvidia/nemotron-4-340b-instruct");
        ids.Insert(19, "qwen/qwen3-235b-a22b");
        ids.Insert(27, "nvidia/llama-3.1-nemotron-70b-instruct");
        Assert.Equal(total, ids.Count);
        Assert.Equal(nemotron, ids.Count(static id => id.Contains("nemotron", StringComparison.Ordinal)));
        Assert.Equal(qwen, ids.Count(static id => id.Contains("qwen", StringComparison.Ordinal)));

        _context.Script("nvidia", ids);
    }

    [Given(@"^the configured ID is absent$")]
    public void GivenTheConfiguredIdIsAbsent()
    {
        Assert.DoesNotContain(ModelCheckContext.ShippedModelId("nvidia"), _context.ListedIds);
        Assert.DoesNotContain(ModelCheckContext.ShippedJudgeModelId(), _context.ListedIds);
    }

    [Given(@"^a fake models response that lacks the configured ID ""([^""]*)""$")]
    public void GivenAFakeModelsResponseThatLacksTheConfiguredId(string missing)
    {
        _context.Settings["AgentJoules:Tiers:nvidia:ModelId"] = missing;
        _context.Script("nvidia", [ModelCheckContext.ShippedModelId("nvidia"), "acme/listed-model"]);
        Assert.DoesNotContain(missing, _context.ListedIds);
    }

    [Given(@"^AgentModelIds maps Writer to an ID absent from the fake models response$")]
    public void GivenAgentModelIdsMapsWriterToAnAbsentId()
    {
        _context.Settings["AgentJoules:Tiers:nebius:AgentModelIds:Writer"] = AbsentWriterModel;
        _context.Script("nebius", [ModelCheckContext.ShippedModelId("nebius"), "acme/listed-model"]);
        Assert.DoesNotContain(AbsentWriterModel, _context.ListedIds);
    }

    [Given(@"^a models endpoint that refuses the connection$")]
    public void GivenAModelsEndpointThatRefusesTheConnection() => _context.Script("nvidia", [ModelCheckContext.ShippedModelId("nvidia")], refuse: true);

    [When(@"^the model check runs$")]
    public async Task WhenTheModelCheckRuns() => await _context.RunAsync("run --tier nvidia --dry-run").WaitAsync(HangGuard);

    [When(@"^the command runs$")]
    public async Task WhenTheCommandRuns() => await _context.RunAsync("run --tier nvidia").WaitAsync(HangGuard);

    [When(@"^""([^""]*)"" is executed$")]
    public async Task WhenCommandIsExecuted(string arguments) => await _context.RunAsync(arguments).WaitAsync(HangGuard);

    [Then(@"^the check passes$")]
    public void ThenTheCheckPasses()
    {
        // The check ran (the models route was asked once, for the tier and the judge together) and passed (exit 0, no failure line, no completion call in a dry run).
        FakeHttpMessageHandler endpoint = _context.Endpoint();

        Assert.True(Result.ExitCode == 0, $"Expected exit code 0 but got {Result.ExitCode}; stderr: {Result.Stderr}");
        Assert.Equal(1, endpoint.Count(FakeRoute.Models));
        Assert.Equal(0, endpoint.Count(FakeRoute.ChatCompletions));
        Assert.Empty(Result.ErrorLines);
        Assert.Empty(Result.DeniedRequests);
    }

    [Then(@"^the output lists (\d+) IDs$")]
    public void ThenTheOutputListsIds(int expected)
    {
        Assert.Equal(4, Result.ExitCode);
        Assert.Equal(expected, IdLines.Count);
        Assert.All(IdLines, id => Assert.Contains(id, _context.ListedIds));
        Assert.Equal(IdLines.Count, IdLines.Distinct(StringComparer.Ordinal).Count());
    }

    [Then(@"^the Nemotron and Qwen IDs appear before the others$")]
    public void ThenNemotronAndQwenComeFirst()
    {
        bool[] preferred = [.. IdLines.Select(static id => id.Contains("nemotron", StringComparison.OrdinalIgnoreCase) || id.Contains("qwen", StringComparison.OrdinalIgnoreCase))];

        Assert.Equal(3, preferred.Count(static flag => flag));
        Assert.All(preferred.Take(3), Assert.True);
        Assert.All(preferred.Skip(3), Assert.False);
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        Assert.True(expected == Result.ExitCode, $"Expected exit code {expected} but got {Result.ExitCode}; stderr: {Result.Stderr}");
        Assert.Empty(Result.DeniedRequests);

        // A refused connection must be the refusal of the models request, not an exit 5 from somewhere else.
        if (expected == 5)
        {
            Assert.Equal(1, _context.Endpoint().Count(FakeRoute.Models));
            Assert.Equal(0, _context.Endpoint().Count(FakeRoute.ChatCompletions));
        }
    }

    [Then(@"^the output contains ""([^""]*)"" and at least one listed ID$")]
    public void ThenTheOutputContainsTheMissingIdAndAListedId(string missing)
    {
        string output = Result.Stdout + "\n" + Result.Stderr;

        Assert.Contains(missing, output, StringComparison.Ordinal);
        Assert.NotEmpty(IdLines);
        Assert.Contains(IdLines, id => _context.ListedIds.Contains(id));
    }

    [Then(@"^the output contains that ID$")]
    public void ThenTheOutputContainsThatId()
    {
        string output = Result.Stdout + "\n" + Result.Stderr;

        Assert.Contains(AbsentWriterModel, output, StringComparison.Ordinal);
        Assert.Contains(AbsentWriterModel, Assert.Single(Result.ErrorLines), StringComparison.Ordinal);
    }

    [Then(@"^the fake handler recorded (\d+) requests to the completions route$")]
    public void ThenTheFakeHandlerRecordedRequestsToTheCompletionsRoute(int expected)
    {
        FakeHttpMessageHandler endpoint = _context.Endpoint();

        Assert.Equal(expected, endpoint.Count(FakeRoute.ChatCompletions));

        // The models route was asked, so "0 completions" is the result of the check and not of a command that never started.
        Assert.True(endpoint.Count(FakeRoute.Models) >= 1, "The models route was never asked.");
    }
}