using System.Text.Json;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.Reporting.Json;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace AgentJoules.UnitTests;

// AJ-047: the shared-window flag and the union-window totals of a concurrent run (REQ-013, EDGE-004). The branches go through the metered client on the
// deterministic meter at a constant 60 W, so every energy here is a fixture value, not a measurement; the concurrent branches await Task.Delay(1 s) on the
// fake clock and complete on one Advance, and the windows are compared by the spans the meter recorded, never by the system clock.
public sealed partial class ExecutionModeTests
{
    [Fact]
    [Trait("Spec", "REQ-013")]
    [Trait("Spec", "EDGE-004")]
    public async Task Concurrent_total_is_union_not_sum()
    {
        await using ServiceProvider provider = Provide(RunMode.Concurrent);
        await using Rig rig = await Rig.CreateAsync(provider.GetRequiredService<IMeterGate>());
        FakeChatClient researcherInner = new(rig.Time) { Hold = Task.Delay(BranchDelay, rig.Time), ResponseDelay = TimeSpan.Zero };
        FakeChatClient writerInner = new(rig.Time) { Hold = Task.Delay(BranchDelay, rig.Time), ResponseDelay = TimeSpan.Zero };
        Task<ChatResponse> researcher = rig.ClientOver(researcherInner, "Researcher").GetResponseAsync(Prompt);
        Task<ChatResponse> writer = rig.ClientOver(writerInner, "Writer").GetResponseAsync(Prompt);

        // Both branches started together and are inside their windows; one advance of the fake clock completes both.
        _ = Assert.Single(researcherInner.Calls);
        _ = Assert.Single(writerInner.Calls);
        Assert.False(researcher.IsCompleted);
        Assert.False(writer.IsCompleted);
        rig.Time.Advance(BranchDelay);
        _ = await Task.WhenAll(researcher, writer).WaitAsync(HangGuard);

        StepReading[] steps = [rig.Reading("Researcher"), rig.Reading("Writer")];
        WindowSpan first = steps[0].Window ?? throw new InvalidOperationException("The reading carries no window.");
        WindowSpan second = steps[1].Window ?? throw new InvalidOperationException("The reading carries no window.");
        Assert.Equal(first, second);
        Assert.True(UnionWindowCalculator.Overlaps(first, second));

        RunDocument document = AssembleRun(Tier.Local, RunMode.Concurrent, steps, [], rig.Meter.ComputeUnion);

        Assert.All(document.Steps, static step => Assert.True(step.Flags.HasFlag(ReadingFlags.SharedWindow), step.Agent));

        // The union of two windows of the same second is that second: 60 W x 1 s = 60 J, where the sum of the two readings counts it twice (120 J).
        UnionEnergy union = rig.Meter.ComputeUnion([first, second]);
        Assert.Equal(union.GrossJoules, document.Totals.GrossJoules);
        Assert.Equal(union.NetJoules, document.Totals.NetJoules);
        Assert.Equal(Metric.Measured(60.0, "J"), document.Totals.GrossJoules);
        Assert.Equal((MetricLabel.NotReported, "no-baseline"), (document.Totals.NetJoules.Label, document.Totals.NetJoules.Reason));
        Assert.Equal(120.0, steps.Sum(static step => step.GrossJoules.Value ?? double.NaN));

        // The flag is derived when the document is assembled: the readings the sink holds are unchanged, so every checkpoint derives it again.
        Assert.All(rig.Sink.Snapshot(), static step => Assert.False(step.Flags.HasFlag(ReadingFlags.SharedWindow)));
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    public async Task Serialized_total_is_the_sum_and_no_reading_is_shared()
    {
        await using ServiceProvider provider = Provide(RunMode.Serialized);
        await using Rig rig = await Rig.CreateAsync(provider.GetRequiredService<IMeterGate>());
        Task<ChatResponse> researcher = rig.ClientOver(new FakeChatClient(rig.Time) { ResponseDelay = BranchDelay }, "Researcher").GetResponseAsync(Prompt);
        Task<ChatResponse> writer = rig.ClientOver(new FakeChatClient(rig.Time) { ResponseDelay = BranchDelay }, "Writer").GetResponseAsync(Prompt);
        _ = await Task.WhenAll(researcher, writer).WaitAsync(HangGuard);

        StepReading[] steps = [rig.Reading("Researcher"), rig.Reading("Writer")];
        RunDocument document = AssembleRun(Tier.Local, RunMode.Serialized, steps, [], rig.Meter.ComputeUnion);

        Assert.All(document.Steps, static step => Assert.False(step.Flags.HasFlag(ReadingFlags.SharedWindow), step.Agent));
        Assert.Equal(Metric.Measured(120.0, "J"), document.Totals.GrossJoules);
        Assert.Equal(steps.Sum(static step => step.GrossJoules.Value ?? double.NaN), document.Totals.GrossJoules.Value);
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    [Trait("Spec", "EDGE-004")]
    public async Task Touching_windows_not_shared()
    {
        await using ServiceProvider provider = Provide(RunMode.Concurrent);
        await using Rig rig = await Rig.CreateAsync(provider.GetRequiredService<IMeterGate>());

        // The second call starts at the instant the first one ended: the first window closes when the clock reads 1 s and the second opens then.
        _ = await rig.ClientOver(new FakeChatClient(rig.Time) { ResponseDelay = BranchDelay }, "Researcher").GetResponseAsync(Prompt).WaitAsync(HangGuard);
        _ = await rig.ClientOver(new FakeChatClient(rig.Time) { ResponseDelay = BranchDelay }, "Writer").GetResponseAsync(Prompt).WaitAsync(HangGuard);

        StepReading[] steps = [rig.Reading("Researcher"), rig.Reading("Writer")];
        WindowSpan first = steps[0].Window ?? throw new InvalidOperationException("The reading carries no window.");
        WindowSpan second = steps[1].Window ?? throw new InvalidOperationException("The reading carries no window.");
        Assert.Equal(first.StopSeconds, second.StartSeconds);
        Assert.False(UnionWindowCalculator.Overlaps(first, second));

        RunDocument document = AssembleRun(Tier.Local, RunMode.Concurrent, steps, [], rig.Meter.ComputeUnion);

        Assert.All(document.Steps, static step => Assert.False(step.Flags.HasFlag(ReadingFlags.SharedWindow), step.Agent));

        // Touching windows stay two segments; the energy is the same as one segment of their joint length would give.
        UnionEnergy union = rig.Meter.ComputeUnion([first, second]);
        Assert.Equal(2, union.SegmentCount);
        Assert.Equal(Metric.Measured(120.0, "J"), document.Totals.GrossJoules);
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    [Trait("Spec", "EDGE-004")]
    public void Concurrent_sums_and_judge_exclusion()
    {
        StepReading[] steps =
        [
            StepReadingFixtures.Metered("Planner", window: new WindowSpan(0.0, 2.0), cost: Metric.Estimated(0.10, "EUR"), inputTokens: Metric.Measured(10, "tokens")),
            StepReadingFixtures.Metered("Researcher", window: new WindowSpan(1.0, 3.0), cost: Metric.Estimated(0.20, "EUR"), inputTokens: Metric.Measured(20, "tokens")),
            StepReadingFixtures.Metered("Writer", window: new WindowSpan(3.0, 4.0), cost: Metric.Estimated(0.05, "USD"), inputTokens: Metric.Measured(30, "tokens")),
            StepReadingFixtures.Metered("Reviewer", window: new WindowSpan(5.0, 6.0), cost: Metric.Estimated(0.01, "USD"), inputTokens: Metric.Measured(40, "tokens")),
        ];
        StepReading[] judge =
        [
            .. Enumerable.Range(1, 4).Select(index => StepReadingFixtures.Metered(
                "Judge" + index,
                window: new WindowSpan(0.5, 10.0),
                cost: Metric.Estimated(9.0, "EUR"),
                inputTokens: Metric.Measured(1000, "tokens"),
                judgedAgent: steps[index - 1].Agent)),
        ];
        List<WindowSpan[]> asked = [];
        UnionEnergy Union(IReadOnlyList<WindowSpan> spans)
        {
            asked.Add([.. spans]);
            return new UnionEnergy(Metric.Measured(240.0, "J"), Metric.Measured(40.0, "J"), unionSeconds: 5.0, segmentCount: 2);
        }

        RunDocument document = AssembleRun(Tier.Local, RunMode.Concurrent, steps, judge, Union, wallClockMs: 6000.0);
        Totals totals = document.Totals;

        // The union is asked once, over the four workflow windows only; the judge windows are never part of it.
        WindowSpan[] spans = Assert.Single(asked);
        Assert.Equal(steps.Select(static step => step.Window!.Value), spans);
        Assert.Equal(Metric.Measured(240.0, "J"), totals.GrossJoules);
        Assert.Equal(Metric.Measured(40.0, "J"), totals.NetJoules);

        // Tokens and cost are summed over the workflow steps (cost per currency, never converted); the wall clock is the given workflow wall time.
        Assert.Equal(Metric.Measured(100.0, "tokens"), totals.InputTokens);
        Assert.Equal(Metric.Measured(320.0, "tokens"), totals.OutputTokens);
        Assert.Equal(["EUR", "USD"], totals.CostByCurrency.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(0.30, totals.CostByCurrency["EUR"].Value ?? double.NaN, 1e-9);
        Assert.Equal(0.06, totals.CostByCurrency["USD"].Value ?? double.NaN, 1e-9);
        Assert.Equal(Metric.Measured(6000.0, "ms"), totals.WallClockMs);

        // The judge readings are summed on their own, and never flagged.
        Assert.Equal(Metric.Measured(4000.0, "tokens"), totals.Judge.InputTokens);
        Assert.Equal(36.0, totals.Judge.CostByCurrency["EUR"].Value ?? double.NaN, 1e-9);
        Assert.All(document.Judge, static reading => Assert.False(reading.Flags.HasFlag(ReadingFlags.SharedWindow)));

        // Planner and Researcher overlap; Writer only touches Researcher and Reviewer stands alone.
        Assert.Equal([true, true, false, false], document.Steps.Select(static step => step.Flags.HasFlag(ReadingFlags.SharedWindow)));
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    [Trait("Spec", "EDGE-004")]
    public void Concurrent_run_records_mode()
    {
        RunInfo run = RunAssembler.BuildRunInfo("20261020T120000Z-local", Tier.Local, ["summarise"], RunMode.Concurrent, 1, StepReadingFixtures.StartUtc, RunStatus.Completed);

        Assert.Equal(RunMode.Concurrent, run.Mode);

        StepReading[] steps =
        [
            StepReadingFixtures.Metered("Researcher", window: new WindowSpan(1.0, 2.0)),
            StepReadingFixtures.Metered("Writer", window: new WindowSpan(1.0, 2.0)),
        ];
        RunDocument document = RunAssembler.Assemble(run, EnvironmentOf(Tier.Local), null, null, steps, [], 2000.0);
        string json = JsonSerializer.Serialize(document, AgentJoulesJsonContext.Default.RunDocument);
        using JsonDocument parsed = JsonDocument.Parse(json);

        // The file records the mode by its lowercase name, carries the flag, and never the in-memory window.
        Assert.Equal("concurrent", parsed.RootElement.GetProperty("run").GetProperty("mode").GetString());
        Assert.All(parsed.RootElement.GetProperty("steps").EnumerateArray(), static step =>
        {
            Assert.Contains("shared-window", step.GetProperty("flags").EnumerateArray().Select(static flag => flag.GetString()));
            Assert.False(step.TryGetProperty("window", out _));
        });
        Assert.DoesNotContain("startSeconds", json, StringComparison.OrdinalIgnoreCase);

        // Read back, the document holds the same steps without their windows: the window lives in memory only.
        RunDocument readBack = JsonSerializer.Deserialize(json, AgentJoulesJsonContext.Default.RunDocument) ?? throw new InvalidOperationException("Nothing was read back.");
        Assert.Equal(RunMode.Concurrent, readBack.Run.Mode);
        Assert.All(readBack.Steps, static step => Assert.Null(step.Window));
        Assert.All(readBack.Steps, static step => Assert.True(step.Flags.HasFlag(ReadingFlags.SharedWindow)));
    }

    [Fact]
    [Trait("Spec", "REQ-013")]
    [Trait("Spec", "EDGE-004")]
    public async Task Hosted_concurrent_totals_are_NA_hosted_tier_never_0()
    {
        StepReading[] steps = [await StepReadingFixtures.HostedAsync(Tier.Nvidia, "Researcher"), await StepReadingFixtures.HostedAsync(Tier.Nvidia, "Writer")];
        bool asked = false;

        RunDocument document = AssembleRun(Tier.Nvidia, RunMode.Concurrent, steps, [], _ =>
        {
            asked = true;
            throw new InvalidOperationException("A hosted tier has no union to compute.");
        });

        Assert.False(asked);
        Assert.Equal(Metric.NotApplicable("hosted-tier"), document.Totals.GrossJoules);
        Assert.Equal(Metric.NotApplicable("hosted-tier"), document.Totals.NetJoules);
        Assert.Equal("Per-step energy overlaps in concurrent mode and does not sum to the run total.", ReportSentences.ConcurrentOverlap);
    }

    private static EnvironmentInfo EnvironmentOf(Tier tier) =>
        RunAssembler.BuildEnvironment(tier, new TierOptions { ModelId = "model-a", ReasoningMode = "on" }, gpu: null, runtime: null, modelLoad: null);

    private static RunDocument AssembleRun(
        Tier tier,
        RunMode mode,
        IReadOnlyList<StepReading> steps,
        IReadOnlyList<StepReading> judge,
        Func<IReadOnlyList<WindowSpan>, UnionEnergy>? union,
        double wallClockMs = 2000.0)
    {
        RunInfo run = RunAssembler.BuildRunInfo("20261020T120000Z-" + TierOptions.NameOf(tier), tier, ["summarise"], mode, 1, StepReadingFixtures.StartUtc, RunStatus.Running);
        return RunAssembler.Assemble(run, EnvironmentOf(tier), probe: null, baseline: null, steps, judge, wallClockMs, union);
    }
}