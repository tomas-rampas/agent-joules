using System.Text.Json;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Live;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Core.Security;
using AgentJoules.Dashboard;
using AgentJoules.TestSupport;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-090 scenario (REQ-032), handed to the step class by Reqnroll context injection: fixture events applied to a <see cref="DashboardSnapshot"/> or sent through a
/// <see cref="DashboardSink"/>, the payloads they produced, the embedded page (as served, with a repository link or without), a page source the rules are applied to, and the real <c>run</c>
/// command whose results file is read back. The rules over the page source are the ones of <see cref="DashboardPageRules"/>, shared with the unit tests. Every clock is a fake one; no HTTP, no model,
/// no GPU and no browser is reached.
/// </summary>
public sealed class LivePageContext : IDisposable
{
    /// <summary>The number of events of the dropped-events scenario.</summary>
    public const int ManySteps = 1500;

    private const int Batch = 500;
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Origin = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private readonly List<LiveEvent> _pending = [];
    private TempWorkspace? _workspace;
    private DashboardSink? _sink;
    private DashboardConnection? _connection;

    /// <summary>The state the fixture events are applied to.</summary>
    public DashboardSnapshot Snapshot { get; private set; } = DashboardFixtures.NewSnapshot();

    /// <summary>The payloads of the last When step, in order.</summary>
    public List<string> Payloads { get; } = [];

    /// <summary>The runtime-built key of the key scenario, or null.</summary>
    public string? Key { get; private set; }

    /// <summary>The page text the scenario reads or checks, or null.</summary>
    public string? Source { get; set; }

    /// <summary>The violations the last rule check found.</summary>
    public IReadOnlyList<string> Violations { get; set; } = [];

    /// <summary>The palettes of the contrast scenarios by theme name.</summary>
    public Dictionary<string, IReadOnlyDictionary<string, string>> Palettes { get; } = new(StringComparer.Ordinal);

    /// <summary>The sum of the net joules of the steps emitted by the dropped-events scenario.</summary>
    public double EmittedNetJoules { get; private set; }

    /// <summary>The results file the run wrote, or null.</summary>
    public JsonDocument? ResultsFile { get; private set; }

    /// <summary>The payloads the dashboard produced during the run of the results-file scenario.</summary>
    public List<string> RunPayloads { get; } = [];

    /// <summary>The repository link the page is rendered with.</summary>
    public static Uri RepositoryUri { get; } = new(string.Concat(Uri.UriSchemeHttps, "://", "example.test/owner/repo"));

    /// <summary>The last payload of the last When step.</summary>
    public string LastPayload => Payloads.Count > 0 ? Payloads[^1] : throw new InvalidOperationException("No event was serialized.");

    /// <summary>Queues an event for the next When step.</summary>
    /// <param name="liveEvent">The event.</param>
    public void Queue(LiveEvent liveEvent) => _pending.Add(liveEvent);

    /// <summary>Applies every queued event to the snapshot, in order, and keeps their payloads.</summary>
    public void ApplyQueued()
    {
        Payloads.Clear();
        foreach (LiveEvent liveEvent in _pending)
        {
            Payloads.Add(Snapshot.Apply(liveEvent));
        }

        _pending.Clear();
    }

    /// <summary>Applies one event immediately and keeps its payload.</summary>
    /// <param name="liveEvent">The event.</param>
    public void ApplyNow(LiveEvent liveEvent) => Payloads.Add(Snapshot.Apply(liveEvent));

    /// <summary>Replaces the snapshot with one that passes its payloads through a redactor holding the runtime-built key.</summary>
    public void UseRedactedSnapshot()
    {
        Key = string.Concat("nvapi-", new string(['Z', 'q', '7', 'K', 'x', '3', 'W', 'm', '9', 'T', 'r', '4', 'L', 'p', '2', 'V']));
        var redactor = new SecretRedactor();
        if (!redactor.Register(Key))
        {
            throw new InvalidOperationException("The redactor refused the key.");
        }

        Snapshot = DashboardFixtures.NewSnapshot(redactor);
    }

    /// <summary>The nine event types, with the key in every string member the events carry, applied to the snapshot, then its snapshot payload.</summary>
    public void SerializeEveryEventType()
    {
        string key = Key ?? throw new InvalidOperationException("No key was built.");
        RunDocument run = RunDocumentFixtures.Committed(Tier.Local);
        RunInfo runInfo = run.Run with { StopReason = "stopped " + key };
        EnvironmentInfo environment = run.Environment with { GpuName = "GPU " + key };
        StepIdentity step = new("Agent-" + key, "model-" + key, Tier.Local, "task-" + key, 1, IsJudge: false);
        Payloads.Clear();
        LiveEvent[] events =
        [
            new RunStartedEvent(Origin, false, runInfo, environment, 85.0),
            new BaselineEvent(Origin, false, run.Baseline!),
            new ProgressEvent(Origin, false, "task-" + key, 1, 3, 1, 3),
            new StepStartedEvent(Origin, false, step, 1.0),
            new StepProgressEvent(Origin, false, step, 3),
            new PowerEvent(Origin, false, Metric.Measured(42.0, "W"), 1.5),
            new StepCompletedEvent(Origin, false, KeyedStep(key)),
            new RunCompletedEvent(Origin, false, runInfo, run.Totals),
        ];
        foreach (LiveEvent liveEvent in events)
        {
            Payloads.Add(Snapshot.Apply(liveEvent));
        }

        Payloads.Add(Snapshot.ToSnapshotPayload(Origin));
    }

    /// <summary>Emits <see cref="ManySteps"/> step readings of the agent Writer into a dashboard sink with one connection nobody reads, so that the connection drops its oldest payloads.</summary>
    /// <returns>The task.</returns>
    public async Task EmitManyStepsAsync()
    {
        _sink = DashboardFixtures.NewSink(new RecordingLogger(), new FakeTimeProvider(Origin));
        _connection = _sink.Connect();
        EmittedNetJoules = 0.0;
        for (int index = 1; index <= ManySteps; index++)
        {
            EmittedNetJoules += index;
            _sink.Publish(StepReadingFixtures.Metered(agent: "Writer", netJoules: Metric.Measured(index, "J")));
            if (index % Batch == 0)
            {
                await _sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
            }
        }

        await _sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
    }

    /// <summary>Takes the payloads the connection still holds (the newest 1000).</summary>
    /// <returns>The number of payloads held.</returns>
    public int DrainConnection()
    {
        DashboardConnection connection = _connection ?? throw new InvalidOperationException("No connection was opened.");
        Payloads.Clear();
        while (connection.Events.TryRead(out string? payload))
        {
            Payloads.Add(payload);
        }

        return Payloads.Count;
    }

    /// <summary>Runs <c>run --tier local</c> with the production orchestrator, a scripted GPU and a dashboard sink as the live sink, and reads the results file back.</summary>
    /// <returns>The task.</returns>
    public async Task RunWithDashboardAsync()
    {
        _workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        var gpu = new ScriptedNvmlApi(time) { ConstantPowerWatts = 30.0 };
        RunCommandSetup setup = RunOrchestrationFixtures.Production(
            _workspace.Root,
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["local"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)), ["nvidia"] = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1)) });
        setup.Gpu = gpu;
        RunOrchestrationFixtures.UseClock(setup, time);
        _sink = DashboardFixtures.NewSink(new RecordingLogger(), time);
        _connection = _sink.Connect();
        DashboardSink sink = _sink;

        // No production code emits the step-started event yet, so the dashboard is told that the first step started with a baseline of 20 W; the power samples the meter forwards during the run
        // then feed a real running estimate that the results file must not show.
        sink.Emit(new BaselineEvent(Origin, false, new BaselineResult(Metric.Measured(20.0, "W"), Metric.Measured(0.5, "W"), seconds: 10.0, sampleCount: 100, coefficientOfVariation: 0.025, maxCoefficientOfVariation: 0.1, ReadingFlags.None)));
        sink.Emit(new StepStartedEvent(Origin, false, new StepIdentity("Planner", "model-a", Tier.Local, "summarise", 1, IsJudge: false), 0.0));
        RunOrchestrationFixtures.AddServices(setup, services =>
        {
            _ = services.RemoveAll<ILiveEventSink>();
            _ = services.AddSingleton<ILiveEventSink>(sink);
        });

        CliRunResult result = await RunOrchestrationFixtures.RunDrivenAsync("run --tier local --task incident-brief --repeat 1 --baseline-seconds 2", setup, time).WaitAsync(HangGuard);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"The run exited {result.ExitCode}: {result.Stderr}");
        }

        await sink.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        _ = DrainConnection();
        RunPayloads.AddRange(Payloads);
    }

    /// <summary>Reads the one results file the run wrote.</summary>
    public void ReadResultsFile()
    {
        ResultsFile?.Dispose();
        ResultsFile = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn((_workspace ?? throw new InvalidOperationException("No run happened.")).Root)));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ResultsFile?.Dispose();
        _connection?.Dispose();
        _sink?.Dispose();
        _workspace?.Dispose();
    }

    private static StepReading KeyedStep(string key) =>
        new(
            task: "task-" + key,
            repeat: 1,
            agent: "Agent-" + key,
            modelId: "model-" + key,
            tier: Tier.Local,
            startUtc: StepReadingFixtures.StartUtc,
            endUtc: StepReadingFixtures.StartUtc.AddSeconds(2),
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
            sampleRateHz: 10.0,
            quality: Metric.NotApplicable("judge-failed"),
            qualityReason: "judge-failed: " + key);
}