using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Microsoft.Extensions.AI;

using Reqnroll;

using Xunit;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-047: flag shared windows and total concurrent runs over the union window (REQ-013, EDGE-004). Scoped to the feature so that sentences it shares in
/// wording with other bindings (the AJ-046 flag sentence) resolve here only inside this feature. The state lives in <see cref="ConcurrentUnionContext"/>;
/// the two concurrent branches await <c>Task.Delay(1 s)</c> on the fake clock and both complete on one advance, so their windows are compared by the
/// timestamps the meter recorded and nothing waits on the wall clock. The energy values are fixture values of the deterministic meter, not measurements.
/// </summary>
[Binding]
[Scope(Feature = "AJ-047 Flag shared windows and total concurrent runs over the union window")]
public sealed class ConcurrentUnionSteps(ConcurrentUnionContext context)
{
    private static readonly IReadOnlyList<ChatMessage> Messages = [new ChatMessage(ChatRole.User, "Summarise the report.")];

    // A step that waits on a call must fail, not hang the run, when the code under test leaks (system clock, not the fake one).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan BranchDelay = TimeSpan.FromSeconds(1);

    private readonly ConcurrentUnionContext _context = context;

    [Given(@"^concurrent mode and two branches of 1 second at a constant 60 W on a deterministic meter$")]
    public async Task GivenConcurrentModeAndTwoBranchesOfOneSecond()
    {
        await _context.UseModeAsync(RunMode.Concurrent);
        FakeChatClient researcher = new(_context.Time) { Hold = Task.Delay(BranchDelay, _context.Time), ResponseDelay = TimeSpan.Zero };
        FakeChatClient writer = new(_context.Time) { Hold = Task.Delay(BranchDelay, _context.Time), ResponseDelay = TimeSpan.Zero };
        Task<ChatResponse> first = _context.ClientOver(researcher, "Researcher").GetResponseAsync(Messages);
        Task<ChatResponse> second = _context.ClientOver(writer, "Writer").GetResponseAsync(Messages);

        // Both branches are inside their windows (the no-op gate admits both); one advance of the fake clock completes both delays.
        _ = Assert.Single(researcher.Calls);
        _ = Assert.Single(writer.Calls);
        _context.Time.Advance(BranchDelay);
        _ = await Task.WhenAll(first, second).WaitAsync(HangGuard);
        _context.Steps = _context.Sink.Snapshot();
    }

    [Given(@"^serialized mode and two consecutive 1 second steps at a constant 60 W$")]
    public async Task GivenSerializedModeAndTwoConsecutiveSteps()
    {
        await _context.UseModeAsync(RunMode.Serialized);
        _ = await _context.ClientOver(new FakeChatClient(_context.Time) { ResponseDelay = BranchDelay }, "Researcher").GetResponseAsync(Messages).WaitAsync(HangGuard);
        _ = await _context.ClientOver(new FakeChatClient(_context.Time) { ResponseDelay = BranchDelay }, "Writer").GetResponseAsync(Messages).WaitAsync(HangGuard);
        _context.Steps = _context.Sink.Snapshot();
    }

    [Given(@"^step costs of 0\.10 EUR, 0\.20 EUR and 0\.05 USD$")]
    public void GivenStepCosts()
    {
        _context.Mode = RunMode.Concurrent;
        _context.Steps =
        [
            StepReadingFixtures.Metered("Planner", cost: Metric.Estimated(0.10, "EUR")),
            StepReadingFixtures.Metered("Researcher", cost: Metric.Estimated(0.20, "EUR")),
            StepReadingFixtures.Metered("Writer", cost: Metric.Estimated(0.05, "USD")),
        ];
    }

    [Given(@"^concurrent mode and two windows where the first ends exactly when the second starts$")]
    public async Task GivenConcurrentModeAndTwoTouchingWindows()
    {
        await _context.UseModeAsync(RunMode.Concurrent);
        IMeterWindow first = _context.Meter.OpenWindow();
        _context.Time.Advance(BranchDelay);
        _ = first.Close();
        IMeterWindow second = _context.Meter.OpenWindow();
        _context.Time.Advance(BranchDelay);
        _ = second.Close();

        // The stop of the first window is the very instant the second one opened.
        Assert.Equal(first.Span.StopSeconds, second.Span.StartSeconds);
        _context.Steps =
        [
            StepReadingFixtures.Metered("Researcher", window: first.Span),
            StepReadingFixtures.Metered("Writer", window: second.Span),
        ];
    }

    [Given(@"^concurrent mode on tier ""(.*)"" with unmetered readings$")]
    public async Task GivenConcurrentModeOnAHostedTier(string tier)
    {
        _context.Mode = RunMode.Concurrent;
        _context.Tier = Enum.Parse<Tier>(tier, ignoreCase: true);
        _context.Steps = [await StepReadingFixtures.HostedAsync(_context.Tier, "Researcher"), await StepReadingFixtures.HostedAsync(_context.Tier, "Writer")];
    }

    [Given(@"^(\d+) workflow readings and (\d+) judge readings$")]
    public void GivenWorkflowAndJudgeReadings(int workflow, int judge)
    {
        _context.Mode = RunMode.Concurrent;
        _context.Steps = [.. Enumerable.Range(1, workflow).Select(static index => StepReadingFixtures.Metered("Agent" + index, inputTokens: Metric.Measured(10 * index, "tokens")))];
        _context.Judge = [.. Enumerable.Range(1, judge).Select(static index => StepReadingFixtures.Metered("Judge" + index, inputTokens: Metric.Measured(1000, "tokens"), judgedAgent: "Agent" + index))];
    }

    [When(@"^the run is assembled$")]
    public void WhenTheRunIsAssembled() => _ = _context.Assemble();

    [Then(@"^both readings have flag ""(.*)""$")]
    public void ThenBothReadingsHaveFlag(string flag)
    {
        ReadingFlags parsed = ReadingFlagNames.Parse(flag);
        IReadOnlyList<StepReading> steps = Document().Steps;
        Assert.Equal(2, steps.Count);
        Assert.All(steps, step => Assert.True(step.Flags.HasFlag(parsed), $"The reading of {step.Agent} does not carry the flag {flag}."));
    }

    [Then(@"^the run gross energy equals the union-window energy and not the sum of the two readings$")]
    public void ThenTheRunGrossEnergyIsTheUnion()
    {
        RunDocument document = Document();
        WindowSpan[] spans = [.. _context.Steps.Select(static step => step.Window ?? throw new InvalidOperationException("A step has no window."))];

        // The two windows were recorded at the same instants: one second, both open from the same start to the same stop.
        Assert.Equal(spans[0], spans[1]);
        UnionEnergy union = _context.Meter.ComputeUnion(spans);
        double sum = _context.Steps.Sum(static step => step.GrossJoules.Value ?? double.NaN);
        double gross = document.Totals.GrossJoules.Value ?? double.NaN;

        Assert.Equal(MetricLabel.Measured, document.Totals.GrossJoules.Label);
        Assert.Equal(union.GrossJoules.Value, gross);
        Assert.Equal(60.0, gross);
        Assert.Equal(120.0, sum);
        Assert.NotEqual(sum, gross);
    }

    [Then(@"^the run gross energy equals the sum of the two readings$")]
    public void ThenTheRunGrossEnergyIsTheSum()
    {
        RunDocument document = Document();
        double sum = _context.Steps.Sum(static step => step.GrossJoules.Value ?? double.NaN);

        Assert.Equal(2, _context.Steps.Count);
        Assert.Equal(MetricLabel.Measured, document.Totals.GrossJoules.Label);
        Assert.Equal(sum, document.Totals.GrossJoules.Value);
        Assert.Equal(120.0, sum);
    }

    [Then(@"^no reading has flag ""(.*)""$")]
    public void ThenNoReadingHasFlag(string flag) => AssertNoReadingHas(flag);

    [Then(@"^neither reading has flag ""(.*)""$")]
    public void ThenNeitherReadingHasFlag(string flag) => AssertNoReadingHas(flag);

    [Then(@"^costByCurrency has EUR 0\.30 and USD 0\.05$")]
    public void ThenCostByCurrencyHasTheSums()
    {
        IReadOnlyDictionary<string, Metric> costs = Document().Totals.CostByCurrency;

        // Summed per currency and never converted; 0.1 + 0.2 is not exactly 0.3 in double arithmetic, so the comparison has a tolerance.
        Assert.Equal(["EUR", "USD"], costs.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((MetricLabel.Estimated, "EUR"), (costs["EUR"].Label, costs["EUR"].Currency));
        Assert.Equal(0.30, costs["EUR"].Value ?? double.NaN, 1e-9);
        Assert.Equal((MetricLabel.Estimated, "USD"), (costs["USD"].Label, costs["USD"].Currency));
        Assert.Equal(0.05, costs["USD"].Value ?? double.NaN, 1e-9);
    }

    [Then(@"^totals grossJoules has label ""(.*)"" and a null value$")]
    public void ThenTotalsGrossJoulesHasLabel(string label)
    {
        Metric gross = Document().Totals.GrossJoules;

        Assert.Equal(label, MetricLabelText.Of(gross.Label));
        Assert.Null(gross.Value);
        Assert.Equal(MeterCapabilities.HostedTierReason, gross.Reason);
    }

    [Then(@"^totals inputTokens equals the sum over the (\d+) workflow readings only$")]
    public void ThenTotalsInputTokensIsTheWorkflowSum(int workflow)
    {
        Metric tokens = Document().Totals.InputTokens;
        double expected = _context.Steps.Sum(static step => step.InputTokens.Value ?? double.NaN);

        Assert.Equal(workflow, _context.Steps.Count);
        Assert.Equal(MetricLabel.Measured, tokens.Label);
        Assert.Equal(expected, tokens.Value);
        Assert.Equal(100.0, expected);
    }

    private RunDocument Document() => _context.Document ?? throw new InvalidOperationException("The run was not assembled.");

    private void AssertNoReadingHas(string flag)
    {
        ReadingFlags parsed = ReadingFlagNames.Parse(flag);
        IReadOnlyList<StepReading> steps = Document().Steps;
        Assert.Equal(2, steps.Count);
        Assert.All(steps, step => Assert.False(step.Flags.HasFlag(parsed), $"The reading of {step.Agent} carries the flag {flag}."));
    }
}