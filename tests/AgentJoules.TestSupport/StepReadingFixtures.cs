using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;

using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.TestSupport;

/// <summary>Builds well-formed <see cref="StepReading"/> values for the tests of the reading and the metrics sinks (AJ-040).</summary>
public static class StepReadingFixtures
{
    /// <summary>The start of every fixture reading; the end is two seconds later.</summary>
    public static readonly DateTimeOffset StartUtc = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A reading of a metered tier (local by default) with measured energy, tokens and latency.</summary>
    public static StepReading Metered(string agent = "Planner", Tier tier = Tier.Local) =>
        new(
            task: "summarise",
            repeat: 1,
            agent: agent,
            modelId: "model-a",
            tier: tier,
            startUtc: StartUtc,
            endUtc: StartUtc.AddSeconds(2),
            outcome: StepOutcomes.Ok,
            latencyMs: Metric.Measured(2000, "ms"),
            ttftMs: Metric.Measured(250, "ms"),
            inputTokens: Metric.Measured(120, "tokens"),
            outputTokens: Metric.Measured(80, "tokens"),
            grossJoules: Metric.Measured(180, "J"),
            netJoules: Metric.Measured(60, "J"),
            avgW: Metric.Measured(90, "W"),
            peakW: Metric.Measured(110, "W"),
            cost: Metric.NotReported("not-priced"),
            retries: 0,
            waitMs: 0,
            flags: ReadingFlags.None,
            method: EnergyMethod.Sampling,
            sampleCount: 21,
            sampleRateHz: 10.0);

    /// <summary>
    /// The no-usage reading (AJ-042, EDGE-007): a metered step whose provider sent no usage, so both token metrics are not-reported with the reason
    /// <c>provider-omitted-usage</c> and the cost is N/A with the reason <c>cost-not-configured</c>, never 0. The report-cell half of EDGE-007 (the
    /// Markdown report shows <c>not-reported</c> and <c>N/A</c>, not a number) is carried by <c>RunDocumentFixtures</c>, which is created by AJ-078-T5
    /// (M3) and gets the no-usage row from AJ-083-T2; the run document does not exist before then.
    /// </summary>
    public static StepReading NoUsage(string agent = "Planner", Tier tier = Tier.Local) =>
        new(
            task: "summarise",
            repeat: 1,
            agent: agent,
            modelId: "model-a",
            tier: tier,
            startUtc: StartUtc,
            endUtc: StartUtc.AddSeconds(2),
            outcome: StepOutcomes.Ok,
            latencyMs: Metric.Measured(2000, "ms"),
            ttftMs: Metric.NotApplicable("buffered-call"),
            inputTokens: Metric.NotReported("provider-omitted-usage"),
            outputTokens: Metric.NotReported("provider-omitted-usage"),
            grossJoules: Metric.Measured(180, "J"),
            netJoules: Metric.Measured(60, "J"),
            avgW: Metric.Measured(90, "W"),
            peakW: Metric.Measured(110, "W"),
            cost: Metric.NotApplicable("cost-not-configured"),
            retries: 0,
            waitMs: 0,
            flags: ReadingFlags.None,
            method: EnergyMethod.Sampling,
            sampleCount: 21,
            sampleRateHz: 10.0);

    /// <summary>
    /// A reading of a hosted tier whose energy metrics come from the window of an <see cref="UnmeteredEnergyMeter"/>, so the values are
    /// those the meter produces and not a copy of the expected text.
    /// </summary>
    public static async Task<StepReading> HostedAsync(Tier tier, string agent = "Planner")
    {
        FakeTimeProvider time = new();
        await using UnmeteredEnergyMeter meter = new(time);
        await meter.StartAsync(CancellationToken.None);
        IMeterWindow window = meter.OpenWindow();
        time.Advance(TimeSpan.FromSeconds(2));
        EnergyReading energy = window.Close();

        return new StepReading(
            task: "summarise",
            repeat: 1,
            agent: agent,
            modelId: "model-h",
            tier: tier,
            startUtc: StartUtc,
            endUtc: StartUtc.AddSeconds(2),
            outcome: StepOutcomes.Ok,
            latencyMs: Metric.Measured(2000, "ms"),
            ttftMs: Metric.NotApplicable("buffered-call"),
            inputTokens: Metric.Measured(120, "tokens"),
            outputTokens: Metric.Measured(80, "tokens"),
            grossJoules: energy.GrossJoules,
            netJoules: energy.NetJoules,
            avgW: energy.AverageWatts,
            peakW: energy.PeakWatts,
            cost: Metric.NotReported("not-priced"),
            retries: 0,
            waitMs: 0,
            flags: ReadingFlags.None);
    }
}