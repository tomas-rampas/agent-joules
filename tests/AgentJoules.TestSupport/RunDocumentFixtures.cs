using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Json;

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