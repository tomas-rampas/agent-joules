using AgentJoules.Cli;
using AgentJoules.Core.Live;
using AgentJoules.Dashboard;
using AgentJoules.TestSupport;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-091 scenario (REQ-032, EDGE-018), handed to the step class by Reqnroll context injection: the loopback ports the scenario holds (<see cref="LoopbackPortBlock"/>,
/// P-79), a real <see cref="DashboardSink"/> and <see cref="DashboardServer"/> with the one <see cref="HeadlessSseClient"/> of the scenario (P-76), or a run of the real <c>run</c> command
/// through <see cref="RunCommandHarness"/> with the production orchestrator on a driven fake clock (so the grace after the run takes no wall-clock time), and what the scenario observed.
/// Only the latency scenarios use the system clock (P-80).
/// </summary>
public sealed class DashboardServerContext : IAsyncDisposable
{
    /// <summary>The task of a run that names one task.</summary>
    public const string TaskName = "incident-brief";

    private const string DashboardPortName = "--dashboard-port";

    // A command or a server that hangs must fail the scenario, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    private static readonly DateTimeOffset Origin = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private TempWorkspace? _workspace;
    private TimerTrackingTimeProvider? _time;

    /// <summary>The ports the scenario holds (a listener on each), or a released block whose first port the server may bind.</summary>
    public LoopbackPortBlock? Ports { get; private set; }

    /// <summary>The preferred port the run step appends as <c>--dashboard-port</c> when the command line names none (P-79), or null.</summary>
    public int? PreferredPort { get; private set; }

    /// <summary>The setup of a run of the command, or null for a server scenario.</summary>
    public RunCommandSetup? Setup { get; private set; }

    /// <summary>The command line as it ran, or null.</summary>
    public string? Arguments { get; private set; }

    /// <summary>The result of the run, or null before it ran.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>True when the host of the run had a <see cref="DashboardSink"/> registered.</summary>
    public bool SinkRegistered { get; private set; }

    /// <summary>The live sink the host of the run resolved, or null.</summary>
    public ILiveEventSink? LiveSink { get; private set; }

    /// <summary>The sink of a server scenario.</summary>
    public DashboardSink? Sink { get; private set; }

    /// <summary>The server of a server scenario.</summary>
    public DashboardServer? Server { get; private set; }

    /// <summary>The URI the server printed.</summary>
    public Uri? Uri { get; private set; }

    /// <summary>The sink the latency run emits into: the server's sink, or a delaying sink in front of it.</summary>
    public ILiveEventSink? Target { get; private set; }

    /// <summary>The one client of the scenario.</summary>
    public HeadlessSseClient Client { get; } = new();

    /// <summary>The responses of the scenario, in the order they were requested.</summary>
    public List<HeadlessResponse> Responses { get; } = [];

    /// <summary>The events a client received.</summary>
    public IReadOnlyList<SseReceipt> Receipts { get; set; } = [];

    /// <summary>The content type of the event stream a client read, or null.</summary>
    public string? EventsContentType { get; set; }

    /// <summary>Holds <paramref name="count"/> consecutive loopback ports; the first is the preferred port of the run.</summary>
    /// <param name="count">The number of ports.</param>
    public void HoldPorts(int count)
    {
        Ports = LoopbackPortBlock.Reserve(count);
        PreferredPort = Ports.First;
    }

    /// <summary>Reserves a block of free ports and releases it, so a server or a run with the dashboard binds a port of the scenario's own block, never the default.</summary>
    public void UseFreePorts()
    {
        Ports = LoopbackPortBlock.Reserve(DashboardServer.PortAttempts);
        Ports.Release();
        PreferredPort = Ports.First;
    }

    /// <summary>A run of the command with fake keys, fake models routes for local and nvidia and a scripted GPU (the dry-run scenario).</summary>
    public void UseDryRunSetup()
    {
        _workspace = RunCommandHarness.CreateWorkspace();
        Setup = RunOrchestrationFixtures.Production(
            _workspace.Root,
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["local"] = RunOrchestrationFixtures.Endpoint(), ["nvidia"] = RunOrchestrationFixtures.Endpoint() });
        Setup.Gpu = new ScriptedNvmlApi(new FakeTimeProvider(Origin));
    }

    /// <summary>A hosted run (nvidia, also the judge tier) of the production orchestrator on a driven fake clock.</summary>
    public void UseHostedRunSetup()
    {
        _workspace = RunCommandHarness.CreateWorkspace();
        _time = RunOrchestrationFixtures.Clock();
        Setup = RunOrchestrationFixtures.Production(
            _workspace.Root,
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["nvidia"] = RunOrchestrationFixtures.Endpoint(_time, TimeSpan.FromMilliseconds(10)) });
        RunOrchestrationFixtures.UseClock(Setup, _time);
    }

    /// <summary>
    /// Runs <paramref name="arguments"/>. A command line with <c>--dashboard</c> and without <c>--dashboard-port</c> gets <c>--dashboard-port &lt;port&gt;</c> of the scenario's block
    /// appended (P-79): the unit tests and the scenarios run as two processes, so no scenario may bind the default port.
    /// </summary>
    /// <param name="arguments">The command line of the scenario.</param>
    /// <returns>A task that completes when the command ended.</returns>
    public async Task RunAsync(string arguments)
    {
        RunCommandSetup setup = Setup ?? throw new InvalidOperationException("The scenario has no run setup.");
        bool dashboard = arguments.Split(' ').Contains("--dashboard");
        if (dashboard && !arguments.Contains(DashboardPortName, StringComparison.Ordinal))
        {
            int port = PreferredPort ?? throw new InvalidOperationException("The scenario holds no port block.");
            arguments += " " + DashboardPortName + " " + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Arguments = arguments;
        string root = (_workspace ?? throw new InvalidOperationException("The scenario has no workspace.")).Root;
        CliHostOverrides overrides = setup.ToOverrides(root) with
        {
            OnHostStarted = host =>
            {
                SinkRegistered = host.Services.GetService<DashboardSink>() is not null;
                LiveSink = host.Services.GetRequiredService<ILiveEventSink>();
            },
        };

        Task<CliRunResult> run = CliHarness.RunAsync(arguments, overrides);
        if (_time is not null)
        {
            await FakeTimeDriver.RunWaitsToCompletionAsync(run, _time, maxStep: RunOrchestrationFixtures.SamplingPeriod).WaitAsync(HangGuard);
        }

        Result = await run.WaitAsync(HangGuard);
    }

    /// <summary>The run files the run wrote.</summary>
    /// <returns>The full paths.</returns>
    public string[] RunFiles() => RunOrchestrationFixtures.RunFilesIn((_workspace ?? throw new InvalidOperationException("No run happened.")).Root);

    /// <summary>Starts a real sink and server on the scenario's free ports, on a fake clock or on the system clock.</summary>
    /// <param name="systemClock">True for the latency scenarios.</param>
    /// <returns>A task that completes when the server listens.</returns>
    public async Task StartServerAsync(bool systemClock)
    {
        UseFreePorts();
        Sink = DashboardFixtures.NewSink(new RecordingLogger(), systemClock ? TimeProvider.System : new FakeTimeProvider(Origin));
        Server = new DashboardServer(Sink, NullLoggerFactory.Instance);
        Target = Sink;
        Uri = await Server.TryStartAsync(Ports!.First, null, CancellationToken.None).WaitAsync(HangGuard)
            ?? throw new InvalidOperationException("The dashboard did not start: " + Server.FailureReason);
    }

    /// <summary>Puts a sink that hands every event on <paramref name="delay"/> late in front of the server's sink (P-77).</summary>
    /// <param name="delay">The delay.</param>
    public void DelayDelivery(TimeSpan delay) => Target = new DelayingLiveEventSink(Sink ?? throw new InvalidOperationException("No server was started."), delay, TimeProvider.System);

    /// <summary>Runs the fixture run of the latency check against the server and keeps the receipts.</summary>
    /// <returns>A task that completes when every event arrived.</returns>
    public async Task RunLatencyFixtureAsync() =>
        Receipts = await DashboardLatencyRun.RunAsync(Client, Uri ?? throw new InvalidOperationException("No server was started."), Target!, TimeProvider.System, HangGuard).WaitAsync(HangGuard);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (Server is not null)
        {
            await Server.DisposeAsync();
        }

        if (Sink is not null)
        {
            await Sink.DisposeAsync();
        }

        Ports?.Dispose();
        _workspace?.Dispose();
    }
}