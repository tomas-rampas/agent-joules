using System.Diagnostics;
using System.Text.Json;

using AgentJoules.Core.Tasks;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Tasks;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-056 scenario (orchestrate a tier run over tasks, repeats and modes), handed to the step class by Reqnroll context injection: a workspace with the shipped configuration
/// and the committed tasks, the <see cref="RunCommandSetup"/> of the run with the production orchestrator and a recording fake endpoint per tier, a fake clock and a scripted GPU when the
/// scenario runs the measured tier, and the result. Every scenario runs the real <c>run</c> command through the real command line harness; nothing leaves the process and no process
/// variable is read or set.
/// </summary>
public sealed class RunOrchestrationContext : IDisposable
{
    private readonly Dictionary<string, FakeHttpMessageHandler> _handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HttpMessageHandler> _endpoints = new(StringComparer.Ordinal);
    private readonly List<(string Key, string? Value)> _settings = [];
    private CancellationTokenSource? _cancellation;
    private CliHarnessRun? _started;

    /// <summary>The working directory of the run: the shipped configuration and a copy of the committed tasks.</summary>
    public TempWorkspace Workspace { get; } = RunCommandHarness.CreateWorkspace();

    /// <summary>The committed tasks in file order.</summary>
    public IReadOnlyList<TaskDefinition> Tasks { get; } = TaskSetLoader.Load(Path.Combine(ConfigurationFixtures.RepositoryRoot(), RunCommandHarness.TasksFolder));

    /// <summary>The recording fake endpoints by lowercase tier name.</summary>
    public IReadOnlyDictionary<string, FakeHttpMessageHandler> Handlers => _handlers;

    /// <summary>The fake clock of a measured scenario, or null for a scenario on the system clock.</summary>
    public TimerTrackingTimeProvider? Clock { get; private set; }

    /// <summary>The scripted GPU of a measured scenario, or null.</summary>
    public ScriptedNvmlApi? Gpu { get; private set; }

    /// <summary>The handler that holds the in-flight request of the shutdown scenario, or null.</summary>
    public HoldingHttpMessageHandler? Hold { get; private set; }

    /// <summary>The result of the run, or null before it ended.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The command line of the run, or null before it ran.</summary>
    public string? Arguments { get; private set; }

    /// <summary>How long the command took from the cancellation to its exit (the shutdown scenario); null otherwise.</summary>
    public TimeSpan? ShutdownDuration { get; private set; }

    /// <summary>Removes a configuration key from the run (the variable is absent, not empty): a pair with a null value.</summary>
    /// <param name="key">The key, for example <c>NEBIUS_API_KEY</c>.</param>
    public void Remove(string key) => _settings.Add((key, null));

    /// <summary>Routes the named client of <paramref name="tierName"/> to <paramref name="handler"/>, or to a recording fake that lists the shipped model IDs.</summary>
    /// <param name="tierName">The lowercase tier name.</param>
    /// <param name="handler">The fake endpoint, or null for the default one.</param>
    /// <returns>The fake endpoint.</returns>
    public FakeHttpMessageHandler Route(string tierName, FakeHttpMessageHandler? handler = null)
    {
        FakeHttpMessageHandler fake = handler ?? RunOrchestrationFixtures.Endpoint(Clock, Clock is null ? TimeSpan.Zero : TimeSpan.FromSeconds(1));
        _handlers[tierName] = fake;
        _endpoints[tierName] = fake;
        return fake;
    }

    /// <summary>Puts the fake clock and a scripted GPU at a constant idle power behind the measured tier; call before <see cref="Route"/>, so the endpoints move the same clock.</summary>
    public void UseMeasuredTier()
    {
        Clock = RunOrchestrationFixtures.Clock();
        Gpu = new ScriptedNvmlApi(Clock) { ConstantPowerWatts = 30.0 };
    }

    /// <summary>Routes nvidia through a hold of its <paramref name="number"/>-th completion request (the shutdown scenario).</summary>
    /// <param name="number">The number of the held request, from 1.</param>
    public void HoldCompletion(int number)
    {
        FakeHttpMessageHandler fake = Route("nvidia");
        Hold = new HoldingHttpMessageHandler(fake, FakeRoute.ChatCompletions, number);
        _endpoints["nvidia"] = Hold;
    }

    /// <summary>Runs <paramref name="arguments"/> to its end, driven on the fake clock when the scenario has one.</summary>
    /// <param name="arguments">The command line.</param>
    /// <returns>A task that completes when <see cref="Result"/> is set.</returns>
    public async Task RunAsync(string arguments)
    {
        Arguments = arguments;
        RunCommandSetup setup = Setup();
        Result = Clock is null
            ? await RunCommandHarness.RunAsync(arguments, setup)
            : await RunOrchestrationFixtures.RunDrivenAsync(arguments, setup, Clock);
    }

    /// <summary>Starts <paramref name="arguments"/> without waiting for it, with a command token the scenario cancels later (<see cref="CancelAsync"/>).</summary>
    /// <param name="arguments">The command line.</param>
    public void Start(string arguments)
    {
        Arguments = arguments;
        _cancellation = new CancellationTokenSource();
        _started = CliHarness.Start(arguments, Setup().ToOverrides(Workspace.Root), cancellationToken: _cancellation.Token);
    }

    /// <summary>Cancels the command token of the started run (what Ctrl+C and SIGTERM do) and waits for the command to end, measuring how long the shutdown took.</summary>
    /// <param name="guard">The real time after which a command that did not end fails the scenario.</param>
    /// <returns>A task that completes when <see cref="Result"/> is set.</returns>
    public async Task CancelAsync(TimeSpan guard)
    {
        CliHarnessRun run = _started ?? throw new InvalidOperationException("No run was started.");
        var stopwatch = Stopwatch.StartNew();
        await _cancellation!.CancelAsync();
        Result = await run.Result.WaitAsync(guard);
        ShutdownDuration = stopwatch.Elapsed;
    }

    /// <summary>The run files the command wrote, in ordinal order.</summary>
    /// <returns>The full paths.</returns>
    public string[] RunFiles() => RunOrchestrationFixtures.RunFilesIn(Workspace.Root);

    /// <summary>Reads the one run file of a single-tier run; the caller disposes the document.</summary>
    /// <returns>The document.</returns>
    public JsonDocument ReadSingleFile()
    {
        string[] files = RunFiles();
        return files.Length == 1
            ? RunOrchestrationFixtures.Read(files[0])
            : throw new InvalidOperationException($"Expected one run file but found {files.Length}.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // A scenario that failed before its When step still ends its run, so the harness gate is free for the next scenario.
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            _ = _started?.Result.Wait(TimeSpan.FromSeconds(20));
            _cancellation.Dispose();
        }

        Workspace.Dispose();
    }

    private RunCommandSetup Setup()
    {
        RunCommandSetup setup = RunOrchestrationFixtures.Production(Workspace.Root, _endpoints, [.. _settings]);
        if (Clock is not null)
        {
            setup.Gpu = Gpu;
            RunOrchestrationFixtures.UseClock(setup, Clock);
        }

        return setup;
    }
}