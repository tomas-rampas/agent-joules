using System.Text.Json;

using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-123 (EDGE-016): a cloudgpu run cut off by a lost connection stays on disk as evidence and never becomes a result. The run scenarios run the real <c>run --tier cloudgpu</c> command through <see cref="CloudGpuBudgetRig"/>
/// (the production orchestrator on a fake clock, a fake cloudgpu endpoint that drops the stream of a chosen call); the selection scenarios run <see cref="RunFileSelector"/> on a folder of header stubs. Every step is scoped to the
/// feature because "I run" and "the exit code is 5" are sentences of other stories.
/// </summary>
public sealed partial class CloudGpuSteps
{
    private const string IncompleteFeature = "Keep incomplete cloudgpu run files out of reports";
    private const string IncompleteCommand = "run --tier cloudgpu --repeat 1 --task incident-brief";
    private const string CompletedStub = "20261024T090000Z-cloudgpu.json";
    private const string OlderStub = "20261023T090000Z-cloudgpu.json";

    private static readonly TimeSpan IncompleteHangGuard = TimeSpan.FromSeconds(60);

    private CloudGpuBudgetRig? _incompleteRig;
    private TempWorkspace? _incompleteWorkspace;
    private string? _incompleteSelected;
    private string? _incompleteExpected;
    private string? _incompleteSummary;
    private int _incompleteAttemptsAtDrop;

    private static string IncompleteStub(string status) =>
        $"{{\"schemaVersion\":1,\"run\":{{\"id\":\"r\",\"tier\":\"cloudgpu\",\"status\":\"{status}\",\"flags\":[]}}}}";

    [AfterScenario]
    [Scope(Feature = IncompleteFeature)]
    public void DisposeIncompleteRig()
    {
        _incompleteRig?.Dispose();
        _incompleteWorkspace?.Dispose();
    }

    private CloudGpuBudgetRig IncompleteRig() => _incompleteRig ??= CloudGpuBudgetRig.Create();

    private CliRunResult IncompleteResult() => IncompleteRig().Result ?? throw new XunitException("The command has not run yet.");

    // The workflow fans out after the Planner: the second completion drops its stream after one delta while the third is in flight; a fourth answer exists so that the Reviewer or a retry would be counted.
    private void ScriptTheDrop(bool ampleBudget)
    {
        CloudGpuBudgetRig rig = IncompleteRig();
        rig.Cloud.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.Ok(), FakeAnswer.StreamFailure(1), FakeAnswer.Ok(), FakeAnswer.Ok());
        if (ampleBudget)
        {
            rig.SetObservedCost(1.0);
            rig.SetTierStart(CloudGpuBudgetRig.ClockStart);
        }
    }

    private async Task RunTheCommandAsync(string command) => _ = await IncompleteRig().RunAsync(command).WaitAsync(IncompleteHangGuard);

    private string IncompleteFolder()
    {
        _incompleteWorkspace ??= TempWorkspace.CreateEmpty();
        return Path.Combine(_incompleteWorkspace.Root, "results");
    }

    private void PutStub(string name, string status) =>
        _ = (_incompleteWorkspace ??= TempWorkspace.CreateEmpty()).WriteFile(Path.Combine("results", name), IncompleteStub(status));

    [Given(@"^a fake transport that drops the connection during the second agent call$")]
    [Scope(Feature = IncompleteFeature)]
    public void GivenTheTransportDropsDuringTheSecondCall() => ScriptTheDrop(ampleBudget: false);

    [Given(@"^a fake transport that drops the connection and ample time and budget remain$")]
    [Scope(Feature = IncompleteFeature)]
    public void GivenTheTransportDropsAndBudgetRemains() => ScriptTheDrop(ampleBudget: true);

    [Given(@"^the fake endpoint answers HTTP 429 five times$")]
    [Scope(Feature = IncompleteFeature)]
    public void GivenTheEndpointAnswers429FiveTimes() => IncompleteRig().Cloud.SetStatuses(FakeRoute.ChatCompletions, 429, 429, 429, 429, 429);

    [When(@"^I run ""([^""]*)""$")]
    [Scope(Feature = IncompleteFeature)]
    public async Task WhenIRunTheCommand(string command) => await RunTheCommandAsync(command);

    [When(@"^the run stops$")]
    [Scope(Feature = IncompleteFeature)]
    public async Task WhenTheRunStops()
    {
        await RunTheCommandAsync(IncompleteCommand);
        _incompleteAttemptsAtDrop = IncompleteRig().Completions;
    }

    [Then(@"^the second step has outcome ""([^""]*)"" and run\.status is ""([^""]*)""$")]
    [Scope(Feature = IncompleteFeature)]
    public void ThenTheSecondStepHasOutcomeAndStatus(string outcome, string status)
    {
        using JsonDocument document = IncompleteRig().ReadRunFile();
        JsonElement[] steps = [.. document.RootElement.GetProperty("steps").EnumerateArray()];
        Assert.Equal(2, steps.Length);
        Assert.Equal(outcome, steps[1].GetProperty("outcome").GetString());
        Assert.Equal(status, document.RootElement.GetProperty("run").GetProperty("status").GetString());
    }

    [Then(@"^the failed step has outcome ""([^""]*)""$")]
    [Scope(Feature = IncompleteFeature)]
    public void ThenTheFailedStepHasOutcome(string outcome)
    {
        using JsonDocument document = IncompleteRig().ReadRunFile();
        JsonElement last = document.RootElement.GetProperty("steps").EnumerateArray().Last();
        Assert.Equal(outcome, last.GetProperty("outcome").GetString());
    }

    [Then(@"^run\.status is ""([^""]*)""$")]
    [Scope(Feature = IncompleteFeature)]
    public void ThenTheRunStatusIs(string status)
    {
        using JsonDocument document = IncompleteRig().ReadRunFile();
        Assert.Equal(status, document.RootElement.GetProperty("run").GetProperty("status").GetString());
    }

    [Then(@"^the exit code is (\d+)$")]
    [Scope(Feature = IncompleteFeature)]
    public void ThenTheExitCodeIsFor(int expected)
    {
        CliRunResult result = IncompleteResult();
        Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");
    }

    [Then(@"^the output asks the user to check the VM state in the console$")]
    [Scope(Feature = IncompleteFeature)]
    public void ThenTheOutputAsksToCheckTheVm()
    {
        CliRunResult result = IncompleteResult();
        Assert.Contains("Check the VM state in the console", result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    [Then(@"^the fake recorded no attempt after the drop$")]
    [Scope(Feature = IncompleteFeature)]
    public void ThenNoAttemptFollowsTheDrop()
    {
        // The Planner answered and the second call dropped; the serialized gate had not yet admitted its sibling, which was refused, so the third and fourth answers of the script (the sibling, the Reviewer, or a retry) were never asked for.
        FakeHttpMessageHandler cloud = IncompleteRig().Cloud;
        Assert.Equal(2, _incompleteAttemptsAtDrop);
        Assert.Equal(2, IncompleteRig().Completions);
        int dropOrder = Assert.Single(cloud.Drops).Order;
        Assert.DoesNotContain(cloud.Requests, request => request.Order > dropOrder);
    }

    [Given(@"^a completed cloudgpu run file and an older incomplete one$")]
    [Scope(Feature = IncompleteFeature)]
    public void GivenACompletedFileAndAnOlderIncompleteOne()
    {
        PutStub(OlderStub, "incomplete");
        PutStub(CompletedStub, "completed");
        _incompleteExpected = Path.Combine(IncompleteFolder(), CompletedStub);
    }

    [Given(@"^a copied cloudgpu file with run\.status ""([^""]*)""$")]
    [Scope(Feature = IncompleteFeature)]
    public void GivenACopiedFileWithStatus(string status)
    {
        PutStub(CompletedStub, status);
        _incompleteExpected = null;
    }

    [When(@"^the latest file for tier ""cloudgpu"" is selected$")]
    [Scope(Feature = IncompleteFeature)]
    public void WhenTheLatestFileIsSelected() => _incompleteSelected = RunFileSelector.Latest(IncompleteFolder(), Tier.CloudGpu);

    [Then(@"^the completed file is returned$")]
    [Scope(Feature = IncompleteFeature)]
    public void ThenTheCompletedFileIsReturned() => Assert.Equal(_incompleteExpected, _incompleteSelected);

    [Then(@"^no file is returned$")]
    [Scope(Feature = IncompleteFeature)]
    public void ThenNoFileIsReturned() => Assert.Null(_incompleteSelected);

    [Given(@"^a report input whose only cloudgpu file has status ""incomplete""$")]
    [Scope(Feature = IncompleteFeature)]
    public async Task GivenAReportInputWithOnlyAnIncompleteFile()
    {
        ScriptTheDrop(ampleBudget: false);
        await RunTheCommandAsync(IncompleteCommand);
        using JsonDocument document = IncompleteRig().ReadRunFile();
        Assert.Equal("incomplete", document.RootElement.GetProperty("run").GetProperty("status").GetString());
    }

    [When(@"^the report is rendered$")]
    [Scope(Feature = IncompleteFeature)]
    public async Task WhenTheReportIsRendered() => _incompleteSummary = await IncompleteRig().SummaryAsync().WaitAsync(IncompleteHangGuard);

    [Then(@"^the summary contains ""cloudgpu \| not run"" and no cloudgpu step row$")]
    [Scope(Feature = IncompleteFeature)]
    public void ThenTheSummaryHasNotRunAndNoStepRow()
    {
        string summary = _incompleteSummary ?? throw new XunitException("The report has not been rendered.");
        Assert.Contains("cloudgpu | not run", summary, StringComparison.Ordinal);
        string[] rows = [.. summary.Split('\n').Where(static line => line.StartsWith("| cloudgpu", StringComparison.Ordinal))];
        Assert.Single(rows);
        Assert.Contains("| not run |", rows[0], StringComparison.Ordinal);
    }
}