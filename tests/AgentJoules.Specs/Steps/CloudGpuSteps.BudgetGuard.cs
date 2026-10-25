using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;

using Microsoft.Extensions.Time.Testing;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-122 (EDGE-017): the 4-hour and USD 25 stop. The guard scenarios run <see cref="CloudGpuBudgetGuard"/> on a <see cref="FakeTimeProvider"/>; the run scenarios run the real <c>run</c> command through <see cref="CloudGpuBudgetRig"/>
/// (the production orchestrator on a fake clock, a fake cloudgpu endpoint that counts completions). Every step is scoped to the feature because sentences such as "the exit code is 2" and "I run ..." belong to other stories.
/// </summary>
public sealed partial class CloudGpuSteps
{
    private const string BudgetFeature = "Enforce the 4-hour and USD 25 stop";
    private const string BudgetTask = "incident-brief";
    private const int BudgetRepeats = 3;

    private static readonly TimeSpan BudgetHangGuard = TimeSpan.FromSeconds(60);

    private CloudGpuBudgetRig? _budgetRig;
    private FakeTimeProvider? _budgetClock;
    private CloudGpuOptions? _budgetOptions;
    private BudgetDecision _budgetDecision;
    private bool _budgetChecked;
    private int _budgetCompletionsPerRepeat;

    [GeneratedRegex(@"(\d+) (hour|minute|second)s?")]
    private static partial Regex DurationPart();

    [AfterScenario]
    [Scope(Feature = BudgetFeature)]
    public void DisposeBudgetRig() => _budgetRig?.Dispose();

    private static TimeSpan ParseDuration(string text)
    {
        TimeSpan total = TimeSpan.Zero;
        foreach (Match part in DurationPart().Matches(text))
        {
            double amount = double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
            total += part.Groups[2].Value switch
            {
                "hour" => TimeSpan.FromHours(amount),
                "minute" => TimeSpan.FromMinutes(amount),
                _ => TimeSpan.FromSeconds(amount),
            };
        }

        return total;
    }

    private static void ExitIs(int expected, CliRunResult result) =>
        Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");

    private void StartGuard(double cost, TimeSpan elapsed)
    {
        _budgetClock = new FakeTimeProvider(CloudGpuBudgetRig.ClockStart);
        _budgetOptions = new CloudGpuOptions { ObservedCostUsd = cost, TierStartUtc = _budgetClock.GetUtcNow() };
        _budgetClock.Advance(elapsed);
    }

    private CloudGpuBudgetRig BudgetRig() => _budgetRig ??= CloudGpuBudgetRig.Create();

    private CliRunResult BudgetResult() => BudgetRig().Result ?? throw new XunitException("The command has not run yet.");

    [Given(@"^the observed cost is ([\d.]+) USD and (\d+ hours?(?: \d+ minutes?)?(?: \d+ seconds?)?) (?:has|have) passed(?: since the tier start)?$")]
    [Scope(Feature = BudgetFeature)]
    public void GivenTheObservedCostAndTheTimePassed(string cost, string elapsed) =>
        StartGuard(double.Parse(cost, CultureInfo.InvariantCulture), ParseDuration(elapsed));

    [Given(@"^(\d+ hours?) (?:has|have) passed since the tier start and the observed cost is ([\d.]+) USD$")]
    [Scope(Feature = BudgetFeature)]
    public void GivenTheTimePassedAndTheObservedCost(string elapsed, string cost) =>
        StartGuard(double.Parse(cost, CultureInfo.InvariantCulture), ParseDuration(elapsed));

    [Given(@"^the observed cost is ([\d.]+) USD$")]
    [Scope(Feature = BudgetFeature)]
    public void GivenTheObservedCost(string cost) => StartGuard(double.Parse(cost, CultureInfo.InvariantCulture), TimeSpan.Zero);

    [When(@"^the guard is checked(?: before the run| before the next repeat)?$")]
    [Scope(Feature = BudgetFeature)]
    public void WhenTheGuardIsChecked()
    {
        _budgetDecision = new CloudGpuBudgetGuard(_budgetClock!, _budgetOptions!).Check();
        _budgetChecked = true;
    }

    [Then(@"^the decision is Continue$")]
    [Scope(Feature = BudgetFeature)]
    public void ThenTheDecisionIsContinue()
    {
        Assert.True(_budgetChecked);
        Assert.False(_budgetDecision.Stop);
        Assert.Null(_budgetDecision.Reason);
    }

    [Then(@"^the decision is Stop with reason ""([^""]*)""$")]
    [Then(@"^the stop reason is ""([^""]*)""$")]
    [Scope(Feature = BudgetFeature)]
    public void ThenTheDecisionIsStop(string reason)
    {
        Assert.True(_budgetChecked);
        Assert.True(_budgetDecision.Stop);
        Assert.Equal(reason, _budgetDecision.Reason);
    }

    [Then(@"^""run --tier cloudgpu"" exits 6 and starts no workflow call$")]
    [Scope(Feature = BudgetFeature)]
    public async Task ThenTheRunExitsSixAndStartsNoWorkflowCall()
    {
        // The same cost, reported the way the builder does: through the configuration, before the invocation.
        CloudGpuBudgetRig rig = BudgetRig();
        rig.SetObservedCost(_budgetOptions!.ObservedCostUsd!.Value);
        rig.SetTierStart(CloudGpuBudgetRig.ClockStart);

        ExitIs(6, await rig.RunAsync("run --tier cloudgpu --repeat 1 --task " + BudgetTask).WaitAsync(BudgetHangGuard));
        Assert.Equal(0, rig.Completions);
        Assert.Empty(rig.RunFiles());
    }

    [Given(@"^repeats 1 and 2 of task one finished and the time box expires before repeat 3$")]
    [Scope(Feature = BudgetFeature)]
    public async Task GivenTwoRepeatsFinishedAndTheTimeBoxExpires()
    {
        _budgetCompletionsPerRepeat = await CloudGpuBudgetRig.CompletionsPerRepeatAsync(BudgetTask);
        CloudGpuBudgetRig rig = BudgetRig();
        rig.SetObservedCost(6.0);
        rig.SetTierStart(CloudGpuBudgetRig.ClockStart);
        rig.AdvanceAtCompletion(2 * _budgetCompletionsPerRepeat, TimeSpan.FromHours(4));
    }

    [When(@"^the orchestrator checks the guard$")]
    [Scope(Feature = BudgetFeature)]
    public async Task WhenTheOrchestratorChecksTheGuard() =>
        _ = await BudgetRig().RunAsync("run --tier cloudgpu --repeat " + BudgetRepeats.ToString(CultureInfo.InvariantCulture) + " --task " + BudgetTask).WaitAsync(BudgetHangGuard);

    [Then(@"^no further repeat starts$")]
    [Scope(Feature = BudgetFeature)]
    public void ThenNoFurtherRepeatStarts() => Assert.Equal(2 * _budgetCompletionsPerRepeat, BudgetRig().Completions);

    [Then(@"^the run file has run\.status ""([^""]*)"" and run\.stopReason ""([^""]*)""$")]
    [Scope(Feature = BudgetFeature)]
    public void ThenTheRunFileHasStatusAndStopReason(string status, string reason)
    {
        using JsonDocument document = BudgetRig().ReadRunFile();
        JsonElement run = document.RootElement.GetProperty("run");
        Assert.Equal(status, run.GetProperty("status").GetString());
        Assert.Equal(reason, run.GetProperty("stopReason").GetString());
    }

    [Then(@"^the exit code is 0 and the output tells the user to delete the VM$")]
    [Scope(Feature = BudgetFeature)]
    public void ThenTheExitCodeIsZeroAndTheOutputTellsTheUserToDeleteTheVm()
    {
        CliRunResult result = BudgetResult();
        ExitIs(0, result);
        Assert.Contains("delete the VM", result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    [Given(@"^CloudGpu:CostStopUsd is (\d+)$")]
    [Scope(Feature = BudgetFeature)]
    public void GivenTheStopThresholdIs(int usd) => BudgetRig().Set("CostStopUsd", usd.ToString(CultureInfo.InvariantCulture));

    [When(@"^I run ""([^""]*)""$")]
    [Scope(Feature = BudgetFeature)]
    public async Task WhenIRun(string command) => _ = await BudgetRig().RunAsync(command).WaitAsync(BudgetHangGuard);

    [Then(@"^the exit code is (\d+)$")]
    [Scope(Feature = BudgetFeature)]
    public void ThenTheExitCodeIs(int expected)
    {
        CliRunResult result = BudgetResult();
        ExitIs(expected, result);
        if (expected == 2)
        {
            Assert.Contains("CloudGpu:CostStopUsd", result.Stderr, StringComparison.Ordinal);
        }
    }
}