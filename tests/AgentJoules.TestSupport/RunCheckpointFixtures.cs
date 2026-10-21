using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;

namespace AgentJoules.TestSupport;

/// <summary>
/// Builders for the run-file tests of AJ-055 (REQ-017): a small well-formed <see cref="RunDocument"/> at a given status with a given number of steps, and
/// a step whose cost is a given metric. The wider <c>RunDocumentFixtures</c> of the report stories (AJ-078, M3) replaces nothing here.
/// </summary>
public static class RunCheckpointFixtures
{
    /// <summary>The start of the fixture run, the timestamp the file name carries.</summary>
    public static readonly DateTimeOffset RunStartUtc = new(2026, 10, 22, 9, 15, 30, TimeSpan.Zero);

    /// <summary>The model ID of a Nemotron agent in the fixtures.</summary>
    public const string NemotronModel = "nvidia/nemotron-nano-3-30b-a3b";

    /// <summary>A reading of a hosted-tier (nvidia) step whose cost is <paramref name="cost"/>.</summary>
    /// <param name="cost">The cost metric of the step.</param>
    /// <param name="repeat">The repeat number.</param>
    /// <returns>The reading.</returns>
    public static StepReading StepWithCost(Metric cost, int repeat = 1) =>
        new(
            task: "summarise",
            repeat: repeat,
            agent: "Planner",
            modelId: NemotronModel,
            tier: Tier.Nvidia,
            startUtc: StepReadingFixtures.StartUtc,
            endUtc: StepReadingFixtures.StartUtc.AddSeconds(2),
            outcome: StepOutcomes.Ok,
            latencyMs: Metric.Measured(2000, "ms"),
            ttftMs: Metric.Measured(250, "ms"),
            inputTokens: Metric.Measured(120, "tokens"),
            outputTokens: Metric.Measured(80, "tokens"),
            grossJoules: Metric.NotApplicable("hosted-tier"),
            netJoules: Metric.NotApplicable("hosted-tier"),
            avgW: Metric.NotApplicable("hosted-tier"),
            peakW: Metric.NotApplicable("hosted-tier"),
            cost: cost,
            retries: 0,
            waitMs: 0,
            flags: ReadingFlags.None);

    /// <summary>A document of the nvidia tier at <paramref name="status"/> holding <paramref name="stepCount"/> steps (repeat 1 to <paramref name="stepCount"/>).</summary>
    /// <param name="status">The <c>run.status</c> of the document.</param>
    /// <param name="stepCount">How many steps the document holds.</param>
    /// <param name="cost">The cost of every step; N/A <c>cost-not-configured</c> when null.</param>
    /// <returns>The document.</returns>
    public static RunDocument Document(RunStatus status, int stepCount = 0, Metric? cost = null)
    {
        Metric stepCost = cost ?? Metric.NotApplicable("cost-not-configured");
        StepReading[] steps = [.. Enumerable.Range(1, stepCount).Select(repeat => StepWithCost(stepCost, repeat))];
        Metric notYet = Metric.NotApplicable("run-in-progress");

        return new RunDocument(
            RunDocument.CurrentSchemaVersion,
            new RunInfo("run-fixture", Tier.Nvidia, ["summarise"], RunMode.Serialized, Repeat: 3, RunStartUtc, "0.0.0-test", GitCommit: null, status),
            new EnvironmentInfo(
                GpuName: null,
                DriverVersion: null,
                Os: "test-os",
                DotnetVersion: "10.0.0",
                LocalRuntime: null,
                RuntimeVersion: null,
                GpuOffloadPercent: null,
                ModelLoadSeconds: null,
                PowerSource: null,
                ReasoningMode: "off",
                Temperature: 0.0,
                Provider: null,
                InstancePreset: null,
                Region: null,
                GpuUuid: null,
                ContainerImage: null,
                ContainerDigest: null,
                Precision: null,
                CloudCostUsd: Metric.NotApplicable("no-rented-vm"),
                EnvironmentInfo.GpuBoardOnly,
                new EnvironmentAssumptions(Metric.NotReported("not-configured"), Metric.NotReported("not-configured")),
                [new EnvironmentModel("Planner", NemotronModel, Baseline: false)]),
            Probe: null,
            Baseline: null,
            steps,
            Judge: [],
            new Totals(notYet, notYet, notYet, notYet, new Dictionary<string, Metric>(), notYet, new JudgeTotals(notYet, notYet, new Dictionary<string, Metric>()),
                Gco2e: Metric.NotApplicable("not-configured")));
    }
}