using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Core.Configuration;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Json;
using AgentJoules.Reporting.Results;
using AgentJoules.Workflow.Agents;
using AgentJoules.Workflow.Runs;

namespace AgentJoules.TestSupport;

/// <summary>
/// Builders for the tests of the results model (AJ-078, REQ-015): a well-formed <see cref="RunDocument"/> that holds every label kind (measured, estimated, model-judged, N/A and not-reported), a judge
/// reading, run flags, and a step whose in-memory output text and window are set; the serialiser and the reader of the production context; and the text mutations the negative scenarios use. The small
/// document of <see cref="RunCheckpointFixtures"/> stays what the run-file tests of AJ-055 use.
/// </summary>
public static class RunDocumentFixtures
{
    /// <summary>The in-memory output text of the first step of the fixture document; it must never appear in a serialised document.</summary>
    public const string SecretText = "secret text";

    /// <summary>The throttle reason of the first step of the fixture document unless a caller asks for another.</summary>
    public const string DefaultThrottleReason = "SwPowerCap";

    /// <summary>The agent of the first step, whose output the judge reading scored.</summary>
    public const string PlannerAgent = "Planner";

    /// <summary>The task of every step of the fixture document.</summary>
    public const string TaskName = "summarise";

    /// <summary>The in-memory window of the first step of the fixture document.</summary>
    public static readonly WindowSpan PlannerWindow = new(1.0, 3.0);

    /// <summary>
    /// A local-tier document with two steps and one judge reading: the first step (Planner) carries an estimated cost, a model-judged quality and derived metrics, a throttle reason, the hash and length
    /// of its output text, and the text and a window in memory; the second (Writer) has no usage (not-reported tokens) and no configured cost (N/A); the judge reading names the Planner. The totals hold a
    /// measured, a not-reported and an N/A figure and a cost per currency.
    /// </summary>
    /// <param name="runFlags">The <c>run.flags</c>; none when null.</param>
    /// <param name="throttleReason">The throttle reason of the first step; one of the five names.</param>
    /// <param name="outputText">The in-memory output text of the first step; none (and no hash or length) when null.</param>
    /// <param name="judgeTotalCostUsd">The USD cost in <c>totals.judge.costByCurrency</c>.</param>
    /// <returns>The document.</returns>
    public static RunDocument EveryLabelKind(IReadOnlyList<string>? runFlags = null, string throttleReason = DefaultThrottleReason, string? outputText = SecretText, double judgeTotalCostUsd = 0.001) =>
        Document([PlannerStep(throttleReason, outputText), WriterStep()], [JudgeReading(PlannerAgent)], runFlags, judgeTotalCostUsd);

    /// <summary>The fixture document with the given steps and judge readings; the run, environment, probe, baseline and totals are those of <see cref="EveryLabelKind"/>.</summary>
    /// <param name="steps">The workflow steps.</param>
    /// <param name="judge">The judge readings.</param>
    /// <param name="runFlags">The <c>run.flags</c>; none when null.</param>
    /// <param name="judgeTotalCostUsd">The USD cost in <c>totals.judge.costByCurrency</c>.</param>
    /// <returns>The document.</returns>
    public static RunDocument Document(IReadOnlyList<StepReading> steps, IReadOnlyList<StepReading> judge, IReadOnlyList<string>? runFlags = null, double judgeTotalCostUsd = 0.001) =>
        new(
            RunDocument.CurrentSchemaVersion,
            new RunInfo("run-fixture", Tier.Local, [TaskName], RunMode.Serialized, Repeat: 1, RunCheckpointFixtures.RunStartUtc, "0.1.0", "abc1234", RunStatus.Completed, runFlags, SelfJudged: false),
            Environment(),
            probe: new ProbeResult(true, "Test GPU", "GPU-00000000-0000-0000-0000-000000000000", "555.00", "8.9", "95.00.00.00.01", 300.0, "disabled", true, "counter", 10.0),
            baseline: new BaselineResult(Metric.Measured(30.0, "W"), Metric.Measured(1.5, "W"), seconds: 5.0, sampleCount: 50, coefficientOfVariation: 0.05, maxCoefficientOfVariation: 0.1, ReadingFlags.None),
            steps,
            judge,
            Totals(judgeTotalCostUsd));

    /// <summary>The environment of the fixture document: a local GPU run, <c>cloudCostObservedUtc</c> null.</summary>
    /// <param name="models">The <c>models</c> list; the Planner and the Writer when null.</param>
    /// <returns>The environment.</returns>
    public static EnvironmentInfo Environment(IReadOnlyList<EnvironmentModel>? models = null) =>
        new(
            GpuName: "Test GPU",
            DriverVersion: "555.00",
            Os: "test-os",
            DotnetVersion: "10.0.0",
            LocalRuntime: "ollama",
            RuntimeVersion: "0.1.0",
            GpuOffloadPercent: 100.0,
            ModelLoadSeconds: 4.5,
            PowerSource: null,
            ReasoningMode: "off",
            Temperature: 0.0,
            Provider: "laptop",
            InstancePreset: null,
            Region: null,
            GpuUuid: "GPU-00000000-0000-0000-0000-000000000000",
            ContainerImage: null,
            ContainerDigest: null,
            Precision: "Q4_K_M",
            CloudCostUsd: Metric.NotApplicable("not-cloudgpu"),
            EnvironmentInfo.GpuBoardOnly,
            new EnvironmentAssumptions(Metric.Estimated(0.3, "EUR"), Metric.NotApplicable("not-configured")),
            models ?? [new EnvironmentModel(PlannerAgent, "model-a", Baseline: false), new EnvironmentModel("Writer", "model-b", Baseline: true)],
            CloudCostObservedUtc: null);

    /// <summary>
    /// The totals of the fixture document. They are hand-built, not computed from the steps, so they are not the sums of the fixture readings; a committed example document (AJ-080 <c>Committed()</c>) must
    /// take its totals from <c>RunAssembler</c>.
    /// </summary>
    /// <param name="judgeTotalCostUsd">The USD cost in <c>totals.judge.costByCurrency</c>.</param>
    /// <returns>The totals.</returns>
    public static Totals Totals(double judgeTotalCostUsd = 0.001) =>
        new(
            grossJoules: Metric.Measured(360.0, "J"),
            netJoules: Metric.Measured(120.0, "J"),
            inputTokens: Metric.NotReported("provider-omitted-usage"),
            outputTokens: Metric.Measured(160.0, "tokens"),
            costByCurrency: new Dictionary<string, Metric>(StringComparer.Ordinal) { ["USD"] = Metric.NotApplicable("missing-step-costs") },
            wallClockMs: Metric.Measured(4000.0, "ms"),
            judge: new JudgeTotals(
                Metric.Measured(200.0, "tokens"),
                Metric.Measured(10.0, "tokens"),
                new Dictionary<string, Metric>(StringComparer.Ordinal) { ["USD"] = Metric.Estimated(judgeTotalCostUsd, "USD") }),
            gco2e: Metric.NotApplicable("not-configured"));

    /// <summary>The first step of the fixture document: a measured local step with an estimated cost, a model-judged quality, a throttle reason and the fingerprint of its output text.</summary>
    /// <param name="throttleReason">The throttle reason; one of the five names.</param>
    /// <param name="outputText">The in-memory output text; none (and no hash or length) when null. The window is set whenever a text is.</param>
    /// <returns>The reading.</returns>
    public static StepReading PlannerStep(string throttleReason = DefaultThrottleReason, string? outputText = SecretText) =>
        new(
            task: TaskName,
            repeat: 1,
            agent: PlannerAgent,
            modelId: "model-a",
            tier: Tier.Local,
            startUtc: StepReadingFixtures.StartUtc,
            endUtc: StepReadingFixtures.StartUtc.AddSeconds(2),
            outcome: StepOutcomes.Ok,
            latencyMs: Metric.Measured(2000.0, "ms"),
            ttftMs: Metric.Measured(250.0, "ms"),
            inputTokens: Metric.Measured(120.0, "tokens"),
            outputTokens: Metric.Measured(80.0, "tokens"),
            grossJoules: Metric.Measured(180.0, "J"),
            netJoules: Metric.Measured(60.0, "J"),
            avgW: Metric.Measured(90.0, "W"),
            peakW: Metric.Measured(110.0, "W"),
            cost: Metric.Estimated(0.02, "USD"),
            retries: 0,
            waitMs: 0,
            flags: ReadingFlags.Throttled,
            method: EnergyMethod.Sampling,
            sampleCount: 21,
            sampleRateHz: 10.0,
            throttleReasons: [throttleReason],
            quality: Metric.ModelJudged(4.0),
            joulesPerQualityPoint: Metric.ModelJudged(15.0),
            costPerQualityPoint: Metric.ModelJudgedCost(0.005, "USD"),
            outputSha256: outputText is null ? null : Sha256Hex(outputText),
            outputChars: outputText?.Length,
            outputText: outputText,
            window: outputText is null ? null : PlannerWindow);

    /// <summary>The second step of the fixture document: no usage reported (not-reported tokens), no cost configured (N/A), no first token because the call was buffered (N/A).</summary>
    /// <returns>The reading.</returns>
    public static StepReading WriterStep() =>
        new(
            task: TaskName,
            repeat: 1,
            agent: "Writer",
            modelId: "model-b",
            tier: Tier.Local,
            startUtc: StepReadingFixtures.StartUtc.AddSeconds(2),
            endUtc: StepReadingFixtures.StartUtc.AddSeconds(4),
            outcome: StepOutcomes.Ok,
            latencyMs: Metric.Measured(2000.0, "ms"),
            ttftMs: Metric.NotApplicable("buffered-call"),
            inputTokens: Metric.NotReported("provider-omitted-usage"),
            outputTokens: Metric.NotReported("provider-omitted-usage"),
            grossJoules: Metric.Measured(180.0, "J"),
            netJoules: Metric.Measured(60.0, "J"),
            avgW: Metric.Measured(90.0, "W"),
            peakW: Metric.Measured(110.0, "W"),
            cost: Metric.NotApplicable("cost-not-configured"),
            retries: 0,
            waitMs: 0,
            flags: ReadingFlags.None,
            method: EnergyMethod.Sampling,
            sampleCount: 21,
            sampleRateHz: 10.0);

    /// <summary>A judge reading of the hosted nvidia tier that names <paramref name="judgedAgent"/> and the fixture task and repeat.</summary>
    /// <param name="judgedAgent">The agent whose step output was scored, or null for a reading that names none.</param>
    /// <param name="repeat">The repeat number of the judged step.</param>
    /// <returns>The reading.</returns>
    public static StepReading JudgeReading(string? judgedAgent, int repeat = 1) =>
        new(
            task: TaskName,
            repeat: repeat,
            agent: "Judge",
            modelId: "judge-model",
            tier: Tier.Nvidia,
            startUtc: StepReadingFixtures.StartUtc.AddSeconds(4),
            endUtc: StepReadingFixtures.StartUtc.AddSeconds(5),
            outcome: StepOutcomes.Ok,
            latencyMs: Metric.Measured(1000.0, "ms"),
            ttftMs: Metric.NotApplicable("buffered-call"),
            inputTokens: Metric.Measured(200.0, "tokens"),
            outputTokens: Metric.Measured(10.0, "tokens"),
            grossJoules: Metric.NotApplicable("hosted-tier"),
            netJoules: Metric.NotApplicable("hosted-tier"),
            avgW: Metric.NotApplicable("hosted-tier"),
            peakW: Metric.NotApplicable("hosted-tier"),
            cost: Metric.Estimated(0.001, "USD"),
            retries: 0,
            waitMs: 0,
            flags: ReadingFlags.None,
            judgedAgent: judgedAgent);

    /// <summary>The lowercase hexadecimal SHA-256 of the UTF-8 bytes of a text, the form of <c>outputSha256</c>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Sixty-four lowercase hexadecimal digits.</returns>
    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Serialises a document with the production context of the results model.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The JSON text.</returns>
    public static string Serialise(RunDocument document) => JsonSerializer.Serialize(document, AgentJoulesJsonContext.Default.RunDocument);

    /// <summary>Reads a document with the production context of the results model; a failure surfaces as the exception the reader raises.</summary>
    /// <param name="text">The JSON text.</param>
    /// <returns>The document.</returns>
    public static RunDocument Read(string text) =>
        JsonSerializer.Deserialize(text, AgentJoulesJsonContext.Default.RunDocument) ?? throw new JsonException("The results text holds null.");

    /// <summary>The start of the first step of every committed fixture run file; the timestamp their file names carry.</summary>
    public static readonly DateTimeOffset CommittedStartUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The task names of the committed fixture run files: the three tasks of the repository.</summary>
    public static readonly IReadOnlyList<string> CommittedTaskNames = ["incident-brief", "api-migration", "energy-explainer"];

    /// <summary>The repository-relative folder of the committed fixture run files.</summary>
    public const string CommittedFolder = "tests/AgentJoules.TestSupport/Fixtures/results";

    /// <summary>The model of the judge in the committed fixtures: <c>Judge:ModelId</c> of the shipped configuration.</summary>
    public const string CommittedJudgeModel = "nvidia/nemotron-nano-3-30b-a3b";

    private const int CommittedStepCount = 12;
    private const int CommittedFailedJudgeIndex = 5;
    private const double CommittedWallClockMs = 24000.0;

    /// <summary>The file name of the committed fixture run file of a tier, for example <c>20260101T000000Z-local.json</c>.</summary>
    /// <param name="tier">The local, nvidia or nebius tier.</param>
    /// <returns>The file name.</returns>
    public static string CommittedFileName(Tier tier) => RunFileName.Create(CommittedStartUtc, tier, [CommittedModelId(tier)]);

    /// <summary>
    /// The bytes of a committed fixture run file in the form the results writer produces: the serialised document and one line feed, ASCII.
    /// </summary>
    /// <param name="tier">The local, nvidia or nebius tier.</param>
    /// <returns>The text of the file.</returns>
    public static string CommittedText(Tier tier) => Serialise(Committed(tier)) + "\n";

    /// <summary>
    /// The document behind a committed fixture run file (AJ-080, REQ-015): a completed run of the three committed tasks and the four agents with one repeat, so twelve steps two seconds apart from
    /// 2026-01-01T00:00:00Z, and twelve judge readings on the nvidia tier. The local document holds a probe, a baseline, measured counter energy and estimated EUR costs; the nvidia document holds N/A
    /// energy (<c>hosted-tier</c>), N/A costs (<c>free-tier-no-price</c>) and one quality that is N/A <c>judge-failed: HttpRequestException</c>; the nebius document holds estimated USD costs. The totals
    /// and the derived metrics come from <see cref="RunAssembler"/>, so the document is what the tool would write; nothing in it names an endpoint, a host or a key.
    /// </summary>
    /// <param name="tier">The local, nvidia or nebius tier.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is not local, nvidia or nebius.</exception>
    public static RunDocument Committed(Tier tier)
    {
        string modelId = CommittedModelId(tier);
        string tierName = TierOptions.NameOf(tier);
        var steps = new List<StepReading>();
        foreach (string task in CommittedTaskNames)
        {
            foreach (string agent in AgentCatalog.Names)
            {
                steps.Add(CommittedStep(tier, task, agent, modelId, steps.Count));
            }
        }

        StepReading[] judge = [.. steps.Select((step, index) => CommittedJudgeReading(tier, step, index))];
        var run = new RunInfo($"fixture-20260101T000000Z-{tierName}", tier, [.. CommittedTaskNames], RunMode.Serialized, Repeat: 1, CommittedStartUtc, "0.0.0-fixture", "fixture", RunStatus.Completed, SelfJudged: tier == Tier.Nvidia);
        bool local = tier == Tier.Local;

        return RunAssembler.Assemble(
            run,
            CommittedEnvironment(tier, modelId),
            probe: local ? new ProbeResult(true, "Fixture GPU", "GPU-00000000-0000-0000-0000-000000000000", "555.00", "8.9", "95.00.00.00.01", 300.0, "disabled", true, "counter", 10.0) : null,
            baseline: local ? new BaselineResult(Metric.Measured(30.0, "W"), Metric.Measured(1.5, "W"), seconds: 30.0, sampleCount: 300, coefficientOfVariation: 0.05, maxCoefficientOfVariation: 0.1, ReadingFlags.None) : null,
            steps,
            judge,
            CommittedWallClockMs);
    }

    private static string CommittedModelId(Tier tier) => tier switch
    {
        Tier.Local => "nemotron-3-nano:30b",
        Tier.Nvidia => CommittedJudgeModel,
        Tier.Nebius => "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), (int)tier, "The committed fixtures cover the local, nvidia and nebius tiers."),
    };

    private static EnvironmentInfo CommittedEnvironment(Tier tier, string modelId)
    {
        bool local = tier == Tier.Local;

        return new EnvironmentInfo(
            GpuName: local ? "Fixture GPU" : null,
            DriverVersion: local ? "555.00" : null,
            Os: "fixture-os",
            DotnetVersion: "10.0.0",
            LocalRuntime: local ? "ollama" : null,
            RuntimeVersion: local ? "0.0.0" : null,
            GpuOffloadPercent: local ? 100.0 : null,
            ModelLoadSeconds: local ? 4.5 : null,
            PowerSource: null,
            ReasoningMode: "off",
            Temperature: 0.0,
            Provider: RunAssembler.ProviderOf(tier),
            InstancePreset: null,
            Region: null,
            GpuUuid: local ? "GPU-00000000-0000-0000-0000-000000000000" : null,
            ContainerImage: null,
            ContainerDigest: null,
            Precision: local ? "Q4_K_M" : null,
            CloudCostUsd: Metric.NotApplicable("not-cloudgpu"),
            EnvironmentInfo.GpuBoardOnly,
            new EnvironmentAssumptions(Metric.Estimated(0.3, "EUR"), Metric.NotApplicable("not-configured")),
            [.. AgentCatalog.Names.Select(agent => new EnvironmentModel(agent, modelId, Baseline: false))],
            CloudCostObservedUtc: null);
    }

    private static StepReading CommittedStep(Tier tier, string task, string agent, string modelId, int index)
    {
        bool local = tier == Tier.Local;
        DateTimeOffset start = CommittedStartUtc.AddSeconds(2 * index);
        double gross = 100.0 + index;
        double net = gross - 30.0;
        bool judgeFailed = tier == Tier.Nvidia && index == CommittedFailedJudgeIndex;

        // An ok step has output: production sets the length and the hash of it, and the judge never scores a step without output.
        string output = $"Fixture output of {agent} for {task}.";
        Metric cost = tier switch
        {
            Tier.Local => Metric.Estimated(Math.Round(net * 0.3 / 3_600_000.0, 9), "EUR"),
            Tier.Nebius => Metric.Estimated(Math.Round(0.00004 + (index * 0.000001), 9), "USD"),
            _ => Metric.NotApplicable("free-tier-no-price"),
        };

        return new StepReading(
            task: task,
            repeat: 1,
            agent: agent,
            modelId: modelId,
            tier: tier,
            startUtc: start,
            endUtc: start.AddSeconds(1),
            outcome: StepOutcomes.Ok,
            latencyMs: Metric.Measured(1000.0, "ms"),
            ttftMs: Metric.Measured(250.0, "ms"),
            inputTokens: Metric.Measured(120.0 + (3 * index), "tokens"),
            outputTokens: Metric.Measured(80.0 + index, "tokens"),
            grossJoules: local ? Metric.Measured(gross, "J") : Metric.NotApplicable("hosted-tier"),
            netJoules: local ? Metric.Measured(net, "J") : Metric.NotApplicable("hosted-tier"),
            avgW: local ? Metric.Measured(gross, "W") : Metric.NotApplicable("hosted-tier"),
            peakW: local ? Metric.Measured(gross + 20.0, "W") : Metric.NotApplicable("hosted-tier"),
            cost: cost,
            retries: 0,
            waitMs: 0,
            flags: ReadingFlags.None,
            method: local ? EnergyMethod.Counter : null,
            quality: judgeFailed ? Metric.NotApplicable("judge-failed: HttpRequestException") : Metric.ModelJudged(3.0 + (index % 3)),
            qualityReason: judgeFailed ? "not-scored" : null,
            outputSha256: Sha256Hex(output),
            outputChars: output.Length);
    }

    private static StepReading CommittedJudgeReading(Tier tier, StepReading judged, int index)
    {
        bool failed = tier == Tier.Nvidia && index == CommittedFailedJudgeIndex;

        // The judge reads its step in the second after the step ended, inside the wall time of the repeat (the steps are one second long and start two seconds apart).
        DateTimeOffset start = CommittedStartUtc.AddSeconds((2 * index) + 1);

        // What the judge returns is the score digit; a failed call returns nothing.
        string? reply = failed ? null : ((int)(judged.Quality.Value ?? 0.0)).ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new StepReading(
            task: judged.Task,
            repeat: judged.Repeat,
            agent: "Judge",
            modelId: CommittedJudgeModel,
            tier: Tier.Nvidia,
            startUtc: start,
            endUtc: start.AddSeconds(1),
            outcome: failed ? StepOutcomes.Error : StepOutcomes.Ok,
            latencyMs: Metric.Measured(1000.0, "ms"),
            ttftMs: Metric.NotApplicable("buffered-call"),
            inputTokens: failed ? Metric.NotReported("provider-omitted-usage") : Metric.Measured(200.0 + index, "tokens"),
            outputTokens: failed ? Metric.NotReported("provider-omitted-usage") : Metric.Measured(2.0, "tokens"),
            grossJoules: Metric.NotApplicable("hosted-tier"),
            netJoules: Metric.NotApplicable("hosted-tier"),
            avgW: Metric.NotApplicable("hosted-tier"),
            peakW: Metric.NotApplicable("hosted-tier"),
            cost: Metric.NotApplicable("free-tier-no-price"),
            retries: 0,
            waitMs: 0,
            flags: ReadingFlags.None,
            outputSha256: reply is null ? null : Sha256Hex(reply),
            outputChars: reply?.Length,
            judgedAgent: judged.Agent);
    }

    /// <summary>
    /// A completed nvidia-tier document (AJ-083, EDGE-007) whose one step got a response without usage: both token cells are not-reported (<see cref="TokenCounts.OmittedReason"/>) and the cost is N/A
    /// (<see cref="CostCalculator.TokensNotReportedReason"/>); the energy cells are N/A (<c>hosted-tier</c>). Built in memory through <see cref="RunAssembler"/>; no committed file backs it.
    /// </summary>
    /// <returns>The document.</returns>
    public static RunDocument HostedNoUsage() =>
        ReportDocument(
            Tier.Nvidia,
            [
                ReportRunFixtures.Step(
                    Tier.Nvidia,
                    inputTokens: Metric.NotReported(TokenCounts.OmittedReason),
                    outputTokens: Metric.NotReported(TokenCounts.OmittedReason),
                    cost: Metric.NotApplicable(CostCalculator.TokensNotReportedReason)),
            ]);

    /// <summary>
    /// A completed document of <paramref name="tier"/> (AJ-083, EDGE-004) whose <c>run.mode</c> is <c>concurrent</c>: two agents ran in overlapping windows, so both steps carry the <c>shared-window</c> flag.
    /// </summary>
    /// <param name="tier">The tier of the run.</param>
    /// <returns>The document.</returns>
    public static RunDocument Concurrent(Tier tier) =>
        ReportDocument(
            tier,
            [
                ReportRunFixtures.Step(tier, flags: ReadingFlags.SharedWindow),
                ReportRunFixtures.Step(tier, agent: "Writer", flags: ReadingFlags.SharedWindow),
            ],
            RunMode.Concurrent);

    /// <summary>A completed local document (AJ-083, EDGE-003) whose one step carries the <c>low-resolution</c> flag: the window was too short for the sampling rate.</summary>
    /// <returns>The document.</returns>
    public static RunDocument LowResolution() =>
        ReportDocument(Tier.Local, [ReportRunFixtures.Step(Tier.Local, flags: ReadingFlags.LowResolution)]);

    /// <summary>
    /// A completed local document (AJ-083, EDGE-002) on a GPU without the energy counter: the probe says so, the selector fell back to sampling, and the one step carries the <c>sampling-fallback</c> flag with the
    /// sampling method.
    /// </summary>
    /// <returns>The document.</returns>
    public static RunDocument SamplingFallback() =>
        ReportDocument(
            Tier.Local,
            [ReportRunFixtures.Step(Tier.Local, flags: ReadingFlags.SamplingFallback, method: EnergyMethod.Sampling)],
            probe: new ProbeResult(true, "Test GPU", "GPU-00000000-0000-0000-0000-000000000000", "555.00", "8.9", "95.00.00.00.01", 300.0, "disabled", false, "sampling", 10.0));

    /// <summary>A <c>partial</c> document of <paramref name="tier"/> (AJ-083): the run stopped early on purpose with one finished step and a stop reason.</summary>
    /// <param name="tier">The tier of the run.</param>
    /// <returns>The document.</returns>
    public static RunDocument Partial(Tier tier) =>
        ReportDocument(tier, [ReportRunFixtures.Step(tier)], status: RunStatus.Partial, stopReason: "budget-reached");

    /// <summary>
    /// A <c>partial</c> document of the cloudgpu tier (AJ-087, EDGE-002): the run stopped early with one finished step, the energy method is <c>sampling</c> because it was forced (the step carries no
    /// <c>sampling-fallback</c> flag), the energy is measured and the cost is an estimate in USD (the observed VM cost of the environment is a USD value too, measured because the schema types environment.cloudCostUsd as a measured metric).
    /// </summary>
    /// <returns>The document.</returns>
    public static RunDocument CloudGpuPartialSampling() =>
        ReportDocument(
            Tier.CloudGpu,
            [ReportRunFixtures.Step(Tier.CloudGpu, method: EnergyMethod.Sampling, cost: Metric.Estimated(0.0012, "USD"))],
            status: RunStatus.Partial,
            stopReason: "budget-reached",
            environment: environment => environment with
            {
                GpuName = "Cloud GPU",
                DriverVersion = "555.00",
                Precision = "FP8",
                CloudCostUsd = Metric.Measured(0.45, "USD"),
                CloudCostObservedUtc = CommittedStartUtc.AddHours(1),
            });

    /// <summary>
    /// A completed document of <paramref name="tier"/> (AJ-087, REQ-016) whose judge model is also the model of its workflow step: <c>run.selfJudged</c> is true and the judge reading names the same model ID
    /// as the step. The judge reading is always on the nvidia tier, where the judge runs.
    /// </summary>
    /// <param name="tier">The tier of the run.</param>
    /// <returns>The document.</returns>
    public static RunDocument SelfJudged(Tier tier) =>
        ReportDocument(
            tier,
            [ReportRunFixtures.Step(tier, quality: Metric.ModelJudged(4.0))],
            judge: [ReportRunFixtures.Step(Tier.Nvidia, agent: "Judge", modelId: "model-a", judgedAgent: ReportRunFixtures.Planner)],
            selfJudged: true);

    // A one-task document of the given tier the way the tool would write it: the totals come from the assembler, a measured tier has a probe and a baseline, and no judge ran unless the caller gives readings.
    private static RunDocument ReportDocument(
        Tier tier,
        IReadOnlyList<StepReading> steps,
        RunMode mode = RunMode.Serialized,
        RunStatus status = RunStatus.Completed,
        string? stopReason = null,
        ProbeResult? probe = null,
        IReadOnlyList<StepReading>? judge = null,
        bool selfJudged = false,
        Func<EnvironmentInfo, EnvironmentInfo>? environment = null)
    {
        bool local = tier == Tier.Local;
        bool measured = TierKinds.IsMeasured(tier);
        var run = new RunInfo($"fixture-{TierOptions.NameOf(tier)}", tier, [TaskName], mode, Repeat: 1, CommittedStartUtc, "0.0.0-fixture", "fixture", status, StopReason: stopReason, SelfJudged: selfJudged);
        EnvironmentInfo info = CommittedEnvironment(tier, "model-a");

        return RunAssembler.Assemble(
            run,
            environment is null ? info : environment(info),
            probe: measured ? probe ?? new ProbeResult(true, local ? "Fixture GPU" : "Cloud GPU", "GPU-00000000-0000-0000-0000-000000000000", "555.00", "8.9", "95.00.00.00.01", 300.0, "disabled", true, "counter", 10.0) : null,
            baseline: measured ? new BaselineResult(Metric.Measured(30.0, "W"), Metric.Measured(1.5, "W"), seconds: 30.0, sampleCount: 300, coefficientOfVariation: 0.05, maxCoefficientOfVariation: 0.1, ReadingFlags.None) : null,
            steps,
            judge ?? [],
            wallClockMs: 10000.0);
    }

    /// <summary>Edits the tree of a serialised document and writes it again.</summary>
    /// <param name="json">The serialised document.</param>
    /// <param name="edit">The edit applied to the root object.</param>
    /// <returns>The edited text.</returns>
    public static string Edit(string json, Action<JsonObject> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        JsonObject root = JsonNode.Parse(json)!.AsObject();
        edit(root);
        return root.ToJsonString();
    }

    /// <summary>The text with the first N/A metric (in document order) given a number: the shape a hand-edited file must be refused for.</summary>
    /// <param name="json">The serialised document.</param>
    /// <param name="value">The number to put in <c>value</c>.</param>
    /// <returns>The edited text.</returns>
    public static string WithNaValue(string json, double value) => Edit(json, root => FindMetric(root, "N/A")!["value"] = value);

    /// <summary>The text with the first not-reported metric (in document order) given a number.</summary>
    /// <param name="json">The serialised document.</param>
    /// <param name="value">The number to put in <c>value</c>.</param>
    /// <returns>The edited text.</returns>
    public static string WithNotReportedValue(string json, double value) => Edit(json, root => FindMetric(root, "not-reported")!["value"] = value);

    /// <summary>The text with one more property in the first step.</summary>
    /// <param name="json">The serialised document.</param>
    /// <param name="name">The name of the extra property.</param>
    /// <returns>The edited text.</returns>
    public static string WithExtraStepProperty(string json, string name) => Edit(json, root => root["steps"]!.AsArray()[0]!.AsObject()[name] = 1);

    /// <summary>The text with <c>run.flags</c> replaced by the given strings.</summary>
    /// <param name="json">The serialised document.</param>
    /// <param name="flags">The flag strings, written as they are.</param>
    /// <returns>The edited text.</returns>
    public static string WithRunFlagText(string json, params string[] flags) =>
        Edit(json, root => root["run"]!["flags"] = new JsonArray([.. flags.Select(static flag => (JsonNode?)JsonValue.Create(flag))]));

    // The first object, depth first in document order, whose label is the given text; null when there is none.
    private static JsonObject? FindMetric(JsonNode? node, string label)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["label"] is JsonValue value && value.TryGetValue(out string? text) && text == label)
                {
                    return obj;
                }

                foreach (KeyValuePair<string, JsonNode?> child in obj)
                {
                    if (FindMetric(child.Value, label) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    if (FindMetric(item, label) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            default:
                return null;
        }
    }
}