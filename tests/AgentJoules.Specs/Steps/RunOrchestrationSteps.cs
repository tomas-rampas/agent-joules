using System.Text.Json;

using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-056: orchestrate a tier run over tasks, repeats and modes. Scoped to the feature because sentences such as "the exit code is 2" and "is executed" are reused by other stories with other
/// meanings. The state lives in <see cref="RunOrchestrationContext"/>. Every scenario runs the real <c>run</c> command with the production orchestrator through the real command line harness:
/// the parser, the preflight, the agent pipelines, the workflow, the meter (a scripted GPU behind the real NVML binding on a fake clock for the measured tier), the results writer and the
/// redacting writers are the real ones; the endpoints are recording fakes.
/// </summary>
[Binding]
[Scope(Feature = "AJ-056 Orchestrate a tier run over tasks, repeats and modes")]
public sealed class RunOrchestrationSteps(RunOrchestrationContext context)
{
    // A command that hangs must fail the scenario, not the run (system clock; it only ends a run that already failed).
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    // The termination timeout of the CLI (InvocationConfiguration.ProcessTerminationTimeout): the incomplete checkpoint must be on disk well before it.
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(15);

    private static readonly string[] Separators = [", ", " and "];

    // The two branches after the Planner: the in-flight step of the shutdown scenario is one of them.
    private static readonly string[] Branches = ["Researcher", "Writer"];

    private readonly RunOrchestrationContext _context = context;

    private CliRunResult Result => _context.Result ?? throw new XunitException("The command has not run yet.");

    private string[] StepLines => [.. RunOrchestrationFixtures.StdoutLines(Result).Where(line => _context.Tasks.Any(task => line.StartsWith(task.Name + " r", StringComparison.Ordinal)))];

    [Given(@"^fake clients, (\d+) committed tasks and tier ""([^""]*)""$")]
    public void GivenFakeClientsTasksAndTier(int taskCount, string tierName)
    {
        Assert.Equal(taskCount, _context.Tasks.Count);
        _ = _context.Route(tierName);
    }

    [Given(@"^the committed configuration with cloudgpu disabled and fakes for the other tiers$")]
    public void GivenTheCommittedConfigurationWithFakes()
    {
        Assert.False(ConfigurationFixtures.ShippedOptions().Tiers["cloudgpu"].Enabled);

        // The measured tier waits for its idle baseline on the fake clock, so the endpoints move the same clock.
        _context.UseMeasuredTier();
        foreach (string tierName in new[] { "local", "nvidia", "nebius" })
        {
            _ = _context.Route(tierName);
        }
    }

    [Given(@"^a nvidia step whose response has no usage$")]
    public void GivenANvidiaStepWithoutUsage() =>
        _ = _context.Route("nvidia", new FakeHttpMessageHandler { ModelIds = ConfigurationFixtures.ShippedModelIds(), PromptTokens = null, CompletionTokens = null });

    [Given(@"^the committed tasks$")]
    public void GivenTheCommittedTasks()
    {
        Assert.NotEmpty(_context.Tasks);
        _ = _context.Route("nvidia");
    }

    [Given(@"^NEBIUS_API_KEY is absent and tiers ""([^""]*)"" and ""([^""]*)"" are selected$")]
    public void GivenNebiusKeyAbsent(string first, string second)
    {
        _context.Remove(ProviderClientFixtures.NebiusKeyVariable);
        _ = _context.Route(first);
        _ = _context.Route(second);

        // --tier all also selects local in the committed configuration; its endpoint records too, so "0 requests" covers every selected tier.
        _ = _context.Route("local");
    }

    [Given(@"^the run command$")]
    public void GivenTheRunCommand() => _ = _context.Route("nvidia");

    [Given(@"^a nvidia run whose second step is in flight$")]
    public async Task GivenANvidiaRunWithTheSecondStepInFlight()
    {
        _context.HoldCompletion(number: 2);
        _context.Start($"run --tier nvidia --task {_context.Tasks[0].Name} --repeat 1");
        await _context.Hold!.Reached.WaitAsync(HangGuard);
    }

    [Given(@"^a nvidia run that finishes without interruption$")]
    public async Task GivenANvidiaRunThatFinishes()
    {
        _ = _context.Route("nvidia");
        await _context.RunAsync("run --tier nvidia --repeat 1").WaitAsync(HangGuard);
    }

    [When(@"^""([^""]*)"" is executed$")]
    public async Task WhenCommandIsExecuted(string arguments) => await _context.RunAsync(arguments).WaitAsync(HangGuard);

    [When(@"^the run prints the step line$")]
    public async Task WhenTheRunPrintsTheStepLine() => await _context.RunAsync($"run --tier nvidia --task {_context.Tasks[0].Name} --repeat 1").WaitAsync(HangGuard);

    [When(@"^the cancellation token is signalled$")]
    public async Task WhenTheCancellationTokenIsSignalled() => await _context.CancelAsync(HangGuard);

    [When(@"^the results file is read$")]
    public void WhenTheResultsFileIsRead()
    {
        Assert.Equal(0, Result.ExitCode);
        _ = Assert.Single(_context.RunFiles());
    }

    [Then(@"^the results file has (\d+) steps$")]
    public void ThenTheResultsFileHasSteps(int expected)
    {
        using JsonDocument document = _context.ReadSingleFile();
        JsonElement[] steps = RunOrchestrationFixtures.Steps(document);

        Assert.Equal(expected, steps.Length);
        Assert.Equal(expected, _context.Handlers["nvidia"].Count(FakeRoute.ChatCompletions));
        Assert.Equal(expected, steps.Select(static step => $"{step.GetProperty("task").GetString()}/{step.GetProperty("repeat").GetInt32()}/{step.GetProperty("agent").GetString()}").Distinct(StringComparer.Ordinal).Count());
    }

    [Then(@"^run.status is ""([^""]*)"" and the exit code is (\d+)$")]
    public void ThenRunStatusAndExitCode(string status, int exitCode)
    {
        Assert.True(exitCode == Result.ExitCode, $"Expected exit code {exitCode} but got {Result.ExitCode}; stderr: {Result.Stderr}");
        using JsonDocument document = _context.ReadSingleFile();
        Assert.Equal(status, document.RootElement.GetProperty("run").GetProperty("status").GetString());
    }

    [Then(@"^(\d+) run files exist for tiers (.+)$")]
    public void ThenRunFilesExistForTiers(int count, string list)
    {
        Assert.True(Result.ExitCode == 0, $"Expected exit code 0 but got {Result.ExitCode}; stderr: {Result.Stderr}");
        string[] tiers = [.. list.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Select(static name => name.Trim('"'))];
        string[] files = _context.RunFiles();

        Assert.Equal(count, files.Length);
        Assert.Equal(count, tiers.Length);
        foreach (string tierName in tiers)
        {
            string file = Assert.Single(files, path => Path.GetFileName(path).EndsWith("-" + tierName + ".json", StringComparison.Ordinal));
            using JsonDocument document = RunOrchestrationFixtures.Read(file);
            JsonElement run = document.RootElement.GetProperty("run");
            Assert.Equal(tierName, run.GetProperty("tier").GetString());
            Assert.Equal("completed", run.GetProperty("status").GetString());
        }

        // One command start: every file name carries the same timestamp.
        Assert.Single(files.Select(static path => Path.GetFileName(path).Split('-')[0]).Distinct(StringComparer.Ordinal));
    }

    [Then(@"^the line contains (.+)$")]
    public void ThenTheLineContains(string list)
    {
        string[] fragments = [.. list.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Select(static fragment => fragment.Trim('"'))];
        string[] lines = StepLines;

        Assert.Equal(0, Result.ExitCode);
        Assert.Equal(4, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.All(fragments, fragment => Assert.Contains(fragment, line, StringComparison.Ordinal));
            Assert.DoesNotContain("=0 ", line, StringComparison.Ordinal);
        });
    }

    [Then(@"^the exit code is (\d+)$")]
    public void ThenTheExitCodeIs(int expected)
    {
        Assert.True(expected == Result.ExitCode, $"Expected exit code {expected} but got {Result.ExitCode}; stderr: {Result.Stderr}");
        Assert.Empty(Result.DeniedRequests);

        switch (expected)
        {
            case 2:
                // Refused before any call and before any file: an unknown task by the task set, an invalid mode by the parser.
                Assert.All(_context.Handlers.Values, static handler => Assert.Equal(0, handler.Count(FakeRoute.ChatCompletions)));
                Assert.Empty(_context.RunFiles());
                Assert.Contains(
                    _context.Arguments?.Contains("--mode parallel", StringComparison.Ordinal) == true ? "Must be one of" : "unknown task bogus",
                    Result.Stderr,
                    StringComparison.Ordinal);
                break;
            case 3:
                Assert.EndsWith("(MissingKeyException)", Assert.Single(Result.ErrorLines), StringComparison.Ordinal);
                Assert.Contains(ProviderClientFixtures.NebiusKeyVariable, Result.Stderr, StringComparison.Ordinal);
                break;
        }
    }

    [Then(@"^no run file exists and the fake handler recorded 0 requests$")]
    public void ThenNoRunFileAndNoRequests()
    {
        Assert.Empty(_context.RunFiles());
        Assert.Empty(RunOrchestrationFixtures.EntriesIn(_context.Workspace.Root));
        Assert.All(_context.Handlers.Values, static handler => Assert.Equal(0, handler.RequestCount));
    }

    [Then(@"^the results file has run.status ""([^""]*)"" and the in-flight step has outcome ""([^""]*)""$")]
    public void ThenTheFileIsIncomplete(string status, string outcome)
    {
        using JsonDocument document = _context.ReadSingleFile();
        JsonElement[] steps = RunOrchestrationFixtures.Steps(document);

        Assert.Equal(status, document.RootElement.GetProperty("run").GetProperty("status").GetString());

        // The Planner finished before the hold; the in-flight step is the branch whose request was held, and nothing is left cancelled.
        Assert.Equal(2, steps.Length);
        Assert.Equal(("Planner", "ok"), (steps[0].GetProperty("agent").GetString(), steps[0].GetProperty("outcome").GetString()));
        Assert.Contains(steps[1].GetProperty("agent").GetString(), Branches);
        Assert.Equal(outcome, steps[1].GetProperty("outcome").GetString());
    }

    [Then(@"^the file was written before the termination timeout and the exit code is (\d+)$")]
    public void ThenTheFileWasWrittenInTimeAndTheExitCodeIs(int expected)
    {
        Assert.True(expected == Result.ExitCode, $"Expected exit code {expected} but got {Result.ExitCode}; stderr: {Result.Stderr}");
        Assert.Equal("error: interrupted by shutdown (OperationCanceledException)", Assert.Single(Result.ErrorLines));

        // The command ended, and so the checkpoint was on disk, well inside the termination timeout; no partial file is left.
        Assert.NotNull(_context.ShutdownDuration);
        Assert.True(_context.ShutdownDuration < TerminationTimeout / 3, $"The shutdown took {_context.ShutdownDuration}.");
        string[] names = [.. _context.RunFiles().Select(static path => Path.GetFileName(path))];
        Assert.Equal(names, RunOrchestrationFixtures.EntriesIn(_context.Workspace.Root));
    }

    [Then(@"^run.status is ""([^""]*)"" and no step has outcome ""([^""]*)""$")]
    public void ThenRunStatusAndNoStepHasOutcome(string status, string outcome)
    {
        using JsonDocument document = _context.ReadSingleFile();
        JsonElement[] steps = RunOrchestrationFixtures.Steps(document);

        Assert.Equal(status, document.RootElement.GetProperty("run").GetProperty("status").GetString());
        Assert.Equal(_context.Tasks.Count * 4, steps.Length);
        Assert.All(steps, step => Assert.NotEqual(outcome, step.GetProperty("outcome").GetString()));
        Assert.All(steps, static step => Assert.Equal("ok", step.GetProperty("outcome").GetString()));
    }
}