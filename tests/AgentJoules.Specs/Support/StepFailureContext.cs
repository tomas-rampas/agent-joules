using System.Text.Json;

using AgentJoules.Core.Tasks;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Tasks;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-057 scenario (stop on a failed step, keep partial results and exit 5), handed to the step class by Reqnroll context injection: a workspace with the shipped
/// configuration and the committed tasks, a fake clock that the retry waits of the real retry layer run on, a recording fake endpoint per tier, a recorder around the production
/// orchestrator that keeps every document it writes, and the result. Every scenario runs the real <c>run</c> command through the real command line harness; nothing leaves the process, no
/// process variable is read or set, and no retry wait takes wall-clock time.
/// </summary>
public sealed class StepFailureContext : IDisposable
{
    // A reading needs a call of positive length, and a fake clock does not move by itself: every call takes 10 ms on the clock of the run.
    private static readonly TimeSpan CallDuration = TimeSpan.FromMilliseconds(10);

    private readonly Dictionary<string, FakeHttpMessageHandler> _handlers = new(StringComparer.Ordinal);
    private readonly List<(string Key, string? Value)> _settings = [];

    /// <summary>The working directory of the run: the shipped configuration and a copy of the committed tasks.</summary>
    public TempWorkspace Workspace { get; } = RunCommandHarness.CreateWorkspace();

    /// <summary>The committed tasks in file order.</summary>
    public IReadOnlyList<TaskDefinition> Tasks { get; } = TaskSetLoader.Load(Path.Combine(ConfigurationFixtures.RepositoryRoot(), RunCommandHarness.TasksFolder));

    /// <summary>The fake clock of the run: the retry waits, the latencies and the run file name read it.</summary>
    public TimerTrackingTimeProvider Clock { get; } = RunOrchestrationFixtures.Clock();

    /// <summary>The recorder around the production orchestrator: every document written to every tier's file, in write order.</summary>
    public RecordingOrchestrator Recorder { get; } = new();

    /// <summary>The recording fake endpoints by lowercase tier name.</summary>
    public IReadOnlyDictionary<string, FakeHttpMessageHandler> Handlers => _handlers;

    /// <summary>The command line of the run, or null before it ran.</summary>
    public string? Arguments { get; private set; }

    /// <summary>The result of the run, or null before it ended.</summary>
    public CliRunResult? Result { get; private set; }

    /// <summary>The tier that the scenario scripts to fail while the command is expected to keep the files of the tiers before it, or null.</summary>
    public string? FailedTier { get; set; }

    /// <summary>The completion requests the failed tier is expected to have received: 5 for an exhausted 429, 1 for an answer that is not retried; meaningful with <see cref="FailedTier"/>.</summary>
    public int ExpectedFailedRequests { get; set; }

    /// <summary>The run file read by the scenario, or null; disposed with the context.</summary>
    public JsonDocument? Document { get; set; }

    /// <summary>Routes the named client of <paramref name="tierName"/> to a recording fake that lists the shipped model IDs and answers <paramref name="statuses"/> on the completion route.</summary>
    /// <param name="tierName">The lowercase tier name.</param>
    /// <param name="statuses">The completion statuses in order, the last one repeating; none leaves the default (200 on every call).</param>
    /// <returns>The fake endpoint.</returns>
    public FakeHttpMessageHandler Route(string tierName, params int[] statuses)
    {
        FakeHttpMessageHandler fake = RunOrchestrationFixtures.Endpoint(Clock, CallDuration);
        if (statuses.Length > 0)
        {
            fake.SetStatuses(FakeRoute.ChatCompletions, statuses);
        }

        _handlers[tierName] = fake;
        return fake;
    }

    /// <summary>Sets a configuration pair for the run.</summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The value; null removes the key.</param>
    public void Setting(string key, string? value) => _settings.Add((key, value));

    /// <summary>The fake endpoint of the tier that <paramref name="arguments"/> select with <c>--tier</c>.</summary>
    /// <param name="arguments">The command line of the run.</param>
    /// <returns>The endpoint.</returns>
    public FakeHttpMessageHandler HandlerOf(string arguments)
    {
        string[] words = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int at = Array.IndexOf(words, "--tier");
        return at >= 0 && at + 1 < words.Length && _handlers.TryGetValue(words[at + 1], out FakeHttpMessageHandler? handler)
            ? handler
            : throw new InvalidOperationException("The command line selects no tier with a routed endpoint.");
    }

    /// <summary>Runs <paramref name="arguments"/> to its end on the fake clock.</summary>
    /// <param name="arguments">The command line.</param>
    /// <returns>A task that completes when <see cref="Result"/> is set.</returns>
    public async Task RunAsync(string arguments)
    {
        Arguments = arguments;
        var endpoints = _handlers.ToDictionary(static pair => pair.Key, static pair => (HttpMessageHandler)pair.Value, StringComparer.Ordinal);
        RunCommandSetup setup = RunOrchestrationFixtures.Production(Workspace.Root, endpoints, [.. _settings]);
        RunOrchestrationFixtures.UseClock(setup, Clock);
        RunOrchestrationFixtures.AddServices(setup, Recorder.Register);
        Result = await RunOrchestrationFixtures.RunDrivenAsync(arguments, setup, Clock);
    }

    /// <summary>The run files the command wrote, in ordinal order.</summary>
    /// <returns>The full paths.</returns>
    public string[] RunFiles() => RunOrchestrationFixtures.RunFilesIn(Workspace.Root);

    /// <summary>Reads the run file of <paramref name="tierName"/>; the caller disposes the document.</summary>
    /// <param name="tierName">The lowercase tier name.</param>
    /// <returns>The document.</returns>
    public JsonDocument ReadFile(string tierName) =>
        RunOrchestrationFixtures.Read(RunFiles().Single(path => Path.GetFileName(path).EndsWith("-" + tierName + ".json", StringComparison.Ordinal)));

    /// <inheritdoc />
    public void Dispose()
    {
        Document?.Dispose();
        Workspace.Dispose();
    }
}