using System.Collections.Concurrent;
using System.Text.Json;

using AgentJoules.Cli;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Live;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Dashboard;
using AgentJoules.Metering;
using AgentJoules.Nvml;
using AgentJoules.TestSupport;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-089 scenario (REQ-032, EDGE-019), handed to the step class by Reqnroll context injection: a fan-out over an in-memory sink and a dashboard sink, a dashboard sink with a
/// client that never reads, a meter that forwards its samples to a recording live sink, a metered client that streams or buffers, or the real command line with one configuration key. Every
/// clock is a fake one; the only real-time values are hang guards that end a run that already failed. No HTTP, no model and no real GPU is reached.
/// </summary>
public sealed class SinkFanOutContext : IDisposable
{
    /// <summary>The capacity of the ingress channel and of every connection channel.</summary>
    public const int Capacity = 1000;

    private const int Batch = 500;
    private const int MaxYields = 100_000;
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Origin = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private readonly List<(string Key, string? Value)> _configuration = [];
    private DashboardSink? _dashboard;
    private DashboardConnection? _connection;
    private bool _faultFirstEvent;
    private bool _streaming;

    /// <summary>The logger of the dashboard sink and the fan-out.</summary>
    public RecordingLogger Logger { get; } = new();

    /// <summary>The in-memory target of the fan-out.</summary>
    public InMemoryMetricsSink Memory { get; } = new();

    /// <summary>The fan-out of the scenario, or null.</summary>
    public FanOutMetricsSink? FanOut { get; private set; }

    /// <summary>The dashboard sink of the scenario.</summary>
    public DashboardSink Dashboard => _dashboard ?? throw new InvalidOperationException("The scenario built no dashboard sink.");

    /// <summary>The connection of the scenario.</summary>
    public DashboardConnection Connection => _connection ?? throw new InvalidOperationException("The scenario connected no client.");

    /// <summary>The exception that reached the caller of a publish or an emit, or null.</summary>
    public Exception? CallerFailure { get; private set; }

    /// <summary>The number of events emitted so far.</summary>
    public int Emitted { get; private set; }

    /// <summary>The live sink the host resolved, or null.</summary>
    public ILiveEventSink? ResolvedLiveSink { get; private set; }

    /// <summary>The result of the last command line, or null.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The sampling rate of the meter scenarios.</summary>
    public double SampleRateHz { get; private set; } = 10.0;

    /// <summary>The forwarding interval of the meter scenarios.</summary>
    public int PowerIntervalMs { get; private set; } = 100;

    /// <summary>The meter run with forwarding, or null.</summary>
    public MeterRun? Forwarded { get; private set; }

    /// <summary>The same meter run without forwarding (a run without the dashboard), or null.</summary>
    public MeterRun? Plain { get; private set; }

    /// <summary>The step-progress events of the metered step.</summary>
    public IReadOnlyList<StepProgressEvent> Progress { get; private set; } = [];

    /// <summary>Builds a fan-out over the in-memory sink and either the dashboard sink (connected) or a target that throws on every reading.</summary>
    /// <param name="secondThrows">True for the throwing second target.</param>
    public void CreateFanOut(bool secondThrows)
    {
        IMetricsSink second;
        if (secondThrows)
        {
            second = new ThrowingMetricsSink();
        }
        else
        {
            CreateConnectedDashboard();
            second = Dashboard;
        }

        FanOut = new FanOutMetricsSink(Logger, Memory, second);
    }

    /// <summary>Builds a dashboard sink on a fake clock and connects one client that never reads.</summary>
    public void CreateConnectedDashboard()
    {
        _dashboard = DashboardFixtures.NewSink(Logger, new FakeTimeProvider(Origin));
        _connection = _dashboard.Connect();
    }

    /// <summary>Makes the first event emitted one the pump cannot apply, after a connected dashboard sink is built.</summary>
    public void FaultTheFirstEvent()
    {
        CreateConnectedDashboard();
        _faultFirstEvent = true;
    }

    /// <summary>Publishes <paramref name="count"/> readings through the fan-out and keeps whatever reached the caller.</summary>
    /// <param name="count">The number of readings.</param>
    /// <returns>A task that completes when the dashboard, if any, applied them.</returns>
    public async Task PublishAsync(int count)
    {
        FanOutMetricsSink fanOut = FanOut ?? throw new InvalidOperationException("The scenario built no fan-out.");
        try
        {
            for (int i = 0; i < count; i++)
            {
                fanOut.Publish(StepReadingFixtures.Metered());
            }
        }
        catch (Exception failure)
        {
            CallerFailure = failure;
        }

        if (_dashboard is not null)
        {
            await _dashboard.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        }
    }

    /// <summary>Emits <paramref name="count"/> power events with distinct instants in batches of 500, flushing after each, and keeps whatever reached the caller.</summary>
    /// <param name="count">The number of events.</param>
    /// <returns>A task that completes when every event was applied or dropped.</returns>
    public async Task EmitAsync(int count)
    {
        while (Emitted < count)
        {
            int end = Math.Min(count, Emitted + Batch);
            try
            {
                for (; Emitted < end; Emitted++)
                {
                    LiveEvent next = _faultFirstEvent && Emitted == 0 ? new UnknownEvent(At(Emitted)) : new PowerEvent(At(Emitted), false, Metric.Measured(42.0, "W"), Emitted / 10.0);
                    Dashboard.Emit(next);
                }
            }
            catch (Exception failure)
            {
                CallerFailure = failure;
                return;
            }

            await Dashboard.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);
        }
    }

    /// <summary>The payloads the connection holds, read without waiting.</summary>
    /// <returns>The payloads, oldest first.</returns>
    public List<string> DrainConnection()
    {
        var payloads = new List<string>();
        while (Connection.Events.TryRead(out string? payload))
        {
            payloads.Add(payload);
        }

        return payloads;
    }

    /// <summary>The <c>type</c> of an envelope.</summary>
    /// <param name="payload">The envelope.</param>
    /// <returns>The type.</returns>
    public static string TypeOf(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("type").GetString()!;
    }

    /// <summary>The <c>atUtc</c> of an envelope.</summary>
    /// <param name="payload">The envelope.</param>
    /// <returns>The instant.</returns>
    public static DateTimeOffset AtUtcOf(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("atUtc").GetDateTimeOffset();
    }

    /// <summary>The emit instant of the event with the given index.</summary>
    /// <param name="index">The index.</param>
    /// <returns>The instant.</returns>
    public static DateTimeOffset At(int index) => Origin.AddMilliseconds(index);

    /// <summary>Resolves the live sink of the composition root through the real command line (no <c>--dashboard</c>).</summary>
    /// <returns>A task that completes when the command ended.</returns>
    public async Task ResolveLiveSinkAsync()
    {
        CliHostOverrides overrides = CliHarness.Overrides() with { OnHostStarted = host => ResolvedLiveSink = host.Services.GetRequiredService<ILiveEventSink>() };
        Result = await CliHarness.RunAsync("probe", overrides).WaitAsync(HangGuard);
    }

    /// <summary>Sets one key, relative to the root section, for the command line.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    public void Set(string key, string value) => _configuration.Add(("AgentJoules:" + key, value));

    /// <summary>Runs <c>probe</c> with the configuration of the scenario.</summary>
    /// <returns>A task that completes when the command ended.</returns>
    public async Task StartAnyCommandAsync() => Result = await CliHarness.RunAsync("probe", CliHarness.Overrides(_configuration)).WaitAsync(HangGuard);

    /// <summary>Configures the meter scenarios.</summary>
    /// <param name="sampleRateHz">The sampling rate.</param>
    /// <param name="powerIntervalMs">The forwarding interval.</param>
    public void UseMeter(double sampleRateHz, int powerIntervalMs)
    {
        SampleRateHz = sampleRateHz;
        PowerIntervalMs = powerIntervalMs;
    }

    /// <summary>Meters a 1 s window twice, with forwarding and without it, over the same script.</summary>
    /// <returns>A task that completes when both runs ended.</returns>
    public async Task MeterOneSecondAsync()
    {
        Forwarded = await MeterOneSecondAsync(PowerIntervalMs);
        Plain = await MeterOneSecondAsync(null);
    }

    /// <summary>Configures the metered step: streaming (100 text chunks, 10 ms apart, 1 s in all) or buffered.</summary>
    /// <param name="streaming">True for the streaming step.</param>
    public void UseStep(bool streaming) => _streaming = streaming;

    /// <summary>Meters the step on a hosted tier (no GPU) with a recording live sink.</summary>
    /// <returns>A task that completes when the step ended.</returns>
    public async Task MeterTheStepAsync()
    {
        var time = new FakeTimeProvider(Origin);
        var live = new RecordingLiveSink();
        var meter = new UnmeteredEnergyMeter(time);
        await meter.StartAsync(CancellationToken.None);
        MeteringServices services = new(meter, NoOpMeterGate.Instance, Memory, NotConfiguredCostCalculator.Instance, TestInstruments.Create(), time) { Live = live };
        FakeChatClient inner = new(time) { Updates = [.. Enumerable.Range(0, 100).Select(static i => new FakeUpdate($"chunk {i} ", TimeSpan.FromMilliseconds(10)))] };
        var client = new MeteredChatClient(inner, new StepIdentity("Writer", "model-a", Tier.Nvidia, "summarise", 1, IsJudge: false), services);
        IReadOnlyList<ChatMessage> prompt = [new ChatMessage(ChatRole.User, "Summarise the report.")];

        if (_streaming)
        {
            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(prompt).WithCancellation(CancellationToken.None))
            {
                _ = update;
            }
        }
        else
        {
            _ = await client.GetResponseAsync(prompt);
        }

        await meter.DisposeAsync();
        Progress = [.. live.Events.OfType<StepProgressEvent>()];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection?.Dispose();
        _dashboard?.Dispose();
    }

    private async Task<MeterRun> MeterOneSecondAsync(int? intervalMs)
    {
        var time = new FakeTimeProvider(Origin);
        var api = new ScriptedNvmlApi(time) { ConstantPowerWatts = 60.0, CounterSupported = true };
        GpuOpenResult open = new NvmlGpuTelemetrySourceFactory(api).Open(0);
        using IGpuTelemetrySource source = open.Source ?? throw new InvalidOperationException(open.Reason);
        int powerBefore = api.PowerCallCount;
        int counterBefore = api.CounterCallCount;
        var live = new RecordingLiveSink();
        PowerForwarding? forwarding = intervalMs is { } interval ? new PowerForwarding(live, interval) : null;
        var meter = new SampledEnergyMeter(source, MeterMethodSelector.Select("auto", source, SampleRateHz), time, forwarding);
        await meter.StartAsync(CancellationToken.None);

        IMeterWindow window = meter.OpenWindow();
        int ticks = (int)Math.Round(SampleRateHz);
        TimeSpan period = TimeSpan.FromSeconds(1.0 / SampleRateHz);
        for (int i = 0; i < ticks; i++)
        {
            int target = api.PowerCallCount + 1;
            time.Advance(period);
            for (int yields = 0; api.PowerCallCount < target; yields++)
            {
                if (yields >= MaxYields)
                {
                    throw new InvalidOperationException("The sampling loop did not take its tick.");
                }

                await Task.Yield();
            }

            _ = meter.SampleCount;
        }

        _ = window.Close();
        await meter.DisposeAsync();
        return new MeterRun(api.PowerCallCount - powerBefore, api.CounterCallCount - counterBefore, ticks, [.. live.Events.OfType<PowerEvent>()]);
    }

    /// <summary>The counts of one metered second.</summary>
    /// <param name="PowerCalls">The NVML power calls of the meter.</param>
    /// <param name="CounterCalls">The NVML counter calls of the meter.</param>
    /// <param name="Ticks">The loop ticks the clock crossed.</param>
    /// <param name="Events">The power events forwarded.</param>
    public sealed record MeterRun(int PowerCalls, int CounterCalls, int Ticks, IReadOnlyList<PowerEvent> Events);

    // A live event type the dashboard does not know: the fault seam of the pump.
    private sealed record UnknownEvent(DateTimeOffset AtUtc) : LiveEvent(AtUtc, false);

    private sealed class ThrowingMetricsSink : IMetricsSink
    {
        public void Publish(StepReading reading) => throw new InvalidDataException("The second target is broken.");
    }

    private sealed class RecordingLiveSink : ILiveEventSink
    {
        private readonly ConcurrentQueue<LiveEvent> _events = new();

        public IReadOnlyList<LiveEvent> Events => [.. _events];

        public void Emit(LiveEvent liveEvent) => _events.Enqueue(liveEvent);
    }
}