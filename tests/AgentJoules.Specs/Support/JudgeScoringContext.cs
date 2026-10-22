using System.Text.Json;

using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Core.Tasks;
using AgentJoules.Metering;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Judge;
using AgentJoules.Workflow.Tasks;

using Microsoft.Extensions.AI;

using Xunit;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-076 scenario (score each ok step with a metered hosted judge), handed to the step class by Reqnroll context injection. Most scenarios run the real
/// <see cref="JudgeService"/> over fake provider clients on a fake clock, with the judge sink as the sink of the metering services; the scenario about the workflow sink runs the real
/// <c>run</c> command with the production orchestrator and the production judge over a fake endpoint that answers the workflow (streamed) and the judge (buffered) from one tier. Nothing
/// leaves the process and no process variable is read or set.
/// </summary>
public sealed class JudgeScoringContext : IDisposable
{
    /// <summary>The model of the judge when a scenario does not say otherwise.</summary>
    public const string DefaultJudgeModel = "judge-model";

    private const string TaskName = "summarise";
    private const string TaskPrompt = "Summarise the incident for the on-call engineer.";
    private const string StepOutput = "The pager fired at three and the disk was full.";
    private const string NebiusModel = "nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B";

    private readonly TimerTrackingTimeProvider _time = new();
    private readonly InMemoryMetricsSink _judgeSink = new();
    private readonly List<FakeChatClient> _clients = [];
    private TempWorkspace? _workspace;

    /// <summary>The steps of the scenario in run order.</summary>
    public List<StepReading> Steps { get; } = [];

    /// <summary>What the fake judge replies.</summary>
    public string Reply { get; set; } = "4";

    /// <summary>The model of the judge.</summary>
    public string JudgeModel { get; set; } = DefaultJudgeModel;

    /// <summary>The totals assembled by a scenario about the sums, or null before they were.</summary>
    public Totals? Totals { get; set; }

    /// <summary>The sum of the input tokens of the judge readings of a scenario about the sums.</summary>
    public double JudgeInputTokenSum { get; set; }

    /// <summary>The result of the judge, or null before it ran.</summary>
    public JudgeResult? Result { get; private set; }

    /// <summary>The judge readings published to the judge sink by the run, in order.</summary>
    public IReadOnlyList<StepReading> JudgeReadings => _judgeSink.Snapshot();

    /// <summary>The calls the fake judge received, in order.</summary>
    public IReadOnlyList<FakeChatCall> Calls => [.. _clients.SelectMany(static client => client.Calls)];

    /// <summary>The run file of the command scenario, parsed; null before the command ran. Disposed with the context.</summary>
    public JsonDocument? Document { get; private set; }

    /// <summary>The standard output of the command scenario.</summary>
    public string? Stdout { get; private set; }

    /// <summary>The step lines the command printed: the lines that start with the name of the task and <c>r</c>; null before the command ran.</summary>
    public IReadOnlyList<string>? StepLines { get; private set; }

    /// <summary>Builds a local-tier step with measured energy, an ok outcome by default and some output text in memory.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="outcome">The outcome.</param>
    /// <param name="netJoules">The measured net energy.</param>
    /// <param name="text">The output text; null for none.</param>
    /// <param name="model">The model of the step.</param>
    /// <returns>The step.</returns>
    public static StepReading Step(string agent, string outcome = StepOutcomes.Ok, double netJoules = 120.0, string? text = StepOutput, string? model = null)
    {
        DateTimeOffset start = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
        return new StepReading(
            task: TaskName,
            repeat: 1,
            agent: agent,
            modelId: model ?? "model-" + agent.ToLowerInvariant(),
            tier: Tier.Local,
            startUtc: start,
            endUtc: start.AddSeconds(2),
            outcome: outcome,
            latencyMs: Metric.Measured(2000, "ms"),
            ttftMs: Metric.Measured(250, "ms"),
            inputTokens: Metric.Measured(120, "tokens"),
            outputTokens: Metric.Measured(80, "tokens"),
            grossJoules: Metric.Measured(netJoules + 60.0, "J"),
            netJoules: Metric.Measured(netJoules, "J"),
            avgW: Metric.Measured(90, "W"),
            peakW: Metric.Measured(110, "W"),
            cost: Metric.NotReported("not-priced"),
            retries: 0,
            waitMs: 0,
            flags: ReadingFlags.None,
            method: EnergyMethod.Sampling,
            sampleCount: 21,
            sampleRateHz: 10.0,
            outputChars: text?.Length,
            outputText: text);
    }

    /// <summary>Builds a judge reading with a known input token count for the given step, as the metered client would publish it.</summary>
    /// <param name="scored">The step the reading scores.</param>
    /// <param name="inputTokens">The input tokens of the call.</param>
    /// <returns>The reading.</returns>
    public static StepReading JudgeReadingOf(StepReading scored, double inputTokens)
    {
        ArgumentNullException.ThrowIfNull(scored);

        return new StepReading(
            scored.Task, scored.Repeat, "Judge", DefaultJudgeModel, scored.Tier, scored.StartUtc, scored.EndUtc, StepOutcomes.Ok, scored.LatencyMs, scored.TtftMs, Metric.Measured(inputTokens, "tokens"), Metric.Measured(1, "tokens"),
            scored.GrossJoules, scored.NetJoules, scored.AvgW, scored.PeakW, scored.Cost, 0, 0, scored.Flags, scored.Method, scored.SampleCount, scored.SampleRateHz,
            judgedAgent: scored.Agent);
    }

    /// <summary>Runs the real judge over <see cref="Steps"/> as the judge of the tier run <see cref="Tier.Local"/>, hosted on <see cref="Tier.Nvidia"/>, and keeps the result.</summary>
    /// <returns>A task that ends when the judge has run.</returns>
    public async Task JudgeAsync()
    {
        await using var meter = new UnmeteredEnergyMeter(_time);
        await meter.StartAsync(CancellationToken.None);
        var providers = new FakeProviderClientFactory(_time, (_, _) =>
        {
            var client = new FakeChatClient(_time) { ResponseText = Reply, ResponseUsage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 1 } };
            _clients.Add(client);
            return client;
        });
        var services = new MeteringServices(meter, NoOpMeterGate.Instance, _judgeSink, NotConfiguredCostCalculator.Instance, TestInstruments.Create(), _time);
        var judge = new JudgeService(Tier.Nvidia, JudgeModel, "Score one step.\n1. a\n2. b\n3. c\n4. d\n5. e\n", providers, new SecretRedactor(), RetryOptions.Default, new Random(4711));

        Result = await judge.ScoreAsync(new JudgeRequest(Tier.Local, Steps, [new TaskDefinition(TaskName, TaskPrompt, 256)], services), CancellationToken.None);
    }

    /// <summary>Runs the real <c>run</c> command on the nebius tier with the production orchestrator and the production judge, judge tier nebius, and keeps the run file and the output.</summary>
    /// <returns>A task that ends when the command has ended.</returns>
    public async Task RunJudgedCommandAsync()
    {
        _workspace = RunCommandHarness.CreateWorkspace();
        string task = TaskSetLoader.Load(Path.Combine(ConfigurationFixtures.RepositoryRoot(), RunCommandHarness.TasksFolder))[0].Name;
        var nebius = new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), ChatText = Reply };
        RunCommandSetup setup = RunOrchestrationFixtures.Production(
            _workspace.Root,
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["nebius"] = nebius },
            ("AgentJoules:Judge:Tier", "nebius"),
            ("AgentJoules:Judge:ModelId", NebiusModel));
        setup.UseProductionJudge = true;

        CliRunResult result = await RunCommandHarness.RunAsync($"run --tier nebius --task {task} --repeat 1", setup).WaitAsync(TimeSpan.FromSeconds(60));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"The command ended with exit code {result.ExitCode}: {result.Stderr}");
        }

        Stdout = result.Stdout;
        StepLines = [.. RunOrchestrationFixtures.StdoutLines(result).Where(line => line.StartsWith(task + " r", StringComparison.Ordinal))];
        Document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(_workspace.Root)));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Document?.Dispose();
        _workspace?.Dispose();
    }
}