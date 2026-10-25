using System.Text.Json;

using AgentJoules.Cli.Output;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Judge;
using AgentJoules.Workflow.Runs;
using AgentJoules.Workflow.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed partial class RunOrchestrationTests
{
    private const string ShutdownLine = "error: interrupted by shutdown (OperationCanceledException)";

    // ---- fix round 1, item 4: a cancellation between tiers starts nothing ----

    [Fact]
    public async Task A_cancellation_between_tiers_writes_no_file_for_the_tier_that_never_started()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint();
        FakeHttpMessageHandler nebius = RunOrchestrationFixtures.Endpoint();
        RunCommandSetup setup = RunOrchestrationFixtures.Production(
            workspace.Root,
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["nvidia"] = nvidia, ["nebius"] = nebius },
            ("AgentJoules:Tiers:local:Enabled", "false"));
        using var cancellation = new CancellationTokenSource();

        // The command token is cancelled the moment the first tier (nvidia) completes, before the second (nebius) starts.
        RunOrchestrationFixtures.AddServices(setup, services =>
        {
            _ = services.RemoveAll<IRunListener>();
            _ = services.AddSingleton<IRunListener>(provider => new CancellingListener(new ConsoleRunListener(provider.GetRequiredService<ConsoleReporter>()), cancellation));
        });

        CliHarnessRun run = CliHarness.Start($"run --tier all --task {TaskNames[0]} --repeat 1", setup.ToOverrides(workspace.Root), cancellationToken: cancellation.Token);
        try
        {
            CliRunResult result = await run.Result.WaitAsync(HangGuard);

            AssertExit(5, result);
            Assert.Equal(ShutdownLine, Assert.Single(result.ErrorLines));
            Assert.EndsWith("-nvidia.json", Assert.Single(RunOrchestrationFixtures.EntriesIn(workspace.Root)), StringComparison.Ordinal);
            using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
            Assert.Equal("completed", Text(RunOf(document), "status"));
            Assert.Equal(0, nebius.Count(FakeRoute.ChatCompletions));
        }
        finally
        {
            await EndAsync(run, cancellation);
        }
    }

    // ---- fix round 1, items 2 and 10: a failed shutdown write warns, keeps the last checkpoint and still exits 5 ----

    // Held request 6 with --repeat 2: repeat 1 completed and was checkpointed. Held request 2 with --repeat 1: the shutdown hit the first (task, repeat) and no checkpoint exists.
    [Theory]
    [InlineData(6, 2, true)]
    [InlineData(2, 1, false)]
    public async Task A_failed_shutdown_write_warns_says_whether_a_checkpoint_was_kept_and_exits_5(int heldRequest, int repeats, bool checkpointKept)
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint();
        var held = new HoldingHttpMessageHandler(nvidia, FakeRoute.ChatCompletions, number: heldRequest);
        RunCommandSetup setup = NvidiaRun(workspace, held);
        var orchestrator = new RecordingOrchestrator(static document => document.Run.Status == RunStatus.Incomplete ? new IOException("disk-detail-9c1e") : null);
        RunOrchestrationFixtures.AddServices(setup, orchestrator.Register);
        using var cancellation = new CancellationTokenSource();

        CliHarnessRun run = CliHarness.Start($"run --tier nvidia --task {TaskNames[0]} --repeat {repeats}", setup.ToOverrides(workspace.Root), cancellationToken: cancellation.Token);
        try
        {
            await held.Reached.WaitAsync(HangGuard);
            await cancellation.CancelAsync();
            CliRunResult result = await run.Result.WaitAsync(HangGuard);

            AssertExit(5, result);
            Assert.Equal(ShutdownLine, Assert.Single(result.ErrorLines));
            string ending = checkpointKept ? "the last complete checkpoint was kept" : "no checkpoint of this tier was written";
            _ = Assert.Single(result.StderrLines, line => line == $"Warning: the run file could not be written after the shutdown (IOException); {ending}");
            Assert.DoesNotContain("disk-detail-9c1e", result.Stderr, StringComparison.Ordinal);
            Assert.Equal(RunStatus.Incomplete, orchestrator.Writes[^1].Status);

            if (checkpointKept)
            {
                // The checkpoint of repeat 1 is the file on disk, whole.
                using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
                Assert.Equal("running", Text(RunOf(document), "status"));
                Assert.Equal(4, RunOrchestrationFixtures.Steps(document).Length);
            }
            else
            {
                // The incomplete write was the tier's first: nothing of the tier is on disk.
                Assert.Equal([RunStatus.Incomplete], orchestrator.Writes.Select(static write => write.Status));
                Assert.Empty(RunOrchestrationFixtures.EntriesIn(workspace.Root));
            }
        }
        finally
        {
            await EndAsync(run, cancellation);
        }
    }

    // ---- fix round 1, item 8: a shutdown inside the idle baseline of the measured tier ----

    [Fact]
    public async Task A_shutdown_inside_the_baseline_writes_an_incomplete_file_without_steps_and_releases_the_GPU()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        using var cancellation = new CancellationTokenSource();
        TimeSpan baseline = TimeSpan.FromSeconds(5);
        TimerTrackingTimeProvider? clock = null;
        int fired = 0;
        Task cancelled = Task.CompletedTask;
        (RunCommandSetup setup, TimerTrackingTimeProvider time, ScriptedNvmlApi gpu, FakeHttpMessageHandler local) = LocalRun(workspace, gpu => gpu.WattsAt = seconds =>
        {
            // The first sample after the baseline wait started cancels the command token, asynchronously, so the meter's lock is not held by the cancellation callbacks.
            if (clock is not null && clock.History.Contains(baseline) && Interlocked.Exchange(ref fired, 1) == 0)
            {
                Volatile.Write(ref cancelled, cancellation.CancelAsync());
            }

            return 30.0;
        });
        clock = time;

        // The clock does not step again until every callback of the cancellation ran (the baseline wait is cancelled by then), so the remaining steps can never end the wait first.
        CliRunResult result = await RunOrchestrationFixtures
            .RunDrivenAsync(
                $"run --tier local --task {TaskNames[0]} --repeat 1 --baseline-seconds 5",
                setup,
                time,
                cancellation.Token,
                beforeStep: () => Volatile.Read(ref cancelled))
            .WaitAsync(HangGuard);

        AssertExit(5, result);
        Assert.Equal(ShutdownLine, Assert.Single(result.ErrorLines));
        Assert.Equal(1, Volatile.Read(ref fired));
        Assert.False(gpu.IsInitialised);
        // The unmetered warm-up is the one completion the endpoint saw: the shutdown came inside the baseline, before any task call.
        Assert.Equal(1, local.Count(FakeRoute.ChatCompletions));

        string file = Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root));
        Assert.Equal([Path.GetFileName(file)], RunOrchestrationFixtures.EntriesIn(workspace.Root));
        using JsonDocument document = RunOrchestrationFixtures.Read(file);
        JsonElement root = document.RootElement;
        Assert.Equal("incomplete", Text(root.GetProperty("run"), "status"));
        Assert.Equal(gpu.Name, Text(root.GetProperty("probe"), "name"));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("baseline").ValueKind);
        Assert.Empty(RunOrchestrationFixtures.Steps(document));
        JsonElement totals = root.GetProperty("totals");
        foreach (string sum in new[] { "grossJoules", "netJoules", "inputTokens", "outputTokens", "wallClockMs" })
        {
            Assert.Equal(("not-reported", "no-steps"), (Text(totals.GetProperty(sum), "label"), Text(totals.GetProperty(sum), "reason")));
        }
    }

    // ---- fix round 1, item 11: the GPU is released after a failed step ----

    [Fact]
    public async Task A_failed_step_on_the_measured_tier_exits_5_and_releases_the_GPU()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        (RunCommandSetup setup, TimerTrackingTimeProvider time, ScriptedNvmlApi gpu, FakeHttpMessageHandler local) = LocalRun(workspace);
        local.SetStatuses(FakeRoute.ChatCompletions, 200, 500);

        CliRunResult result = await RunLocalAsync($"run --tier local --task {TaskNames[0]} --repeat 1 --baseline-seconds 2", setup, time);

        AssertExit(5, result);
        Assert.EndsWith("(StepFailedException)", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
        Assert.Equal(2, gpu.InitCallCount);
        Assert.False(gpu.IsInitialised);
    }

    // ---- fix round 1, item 9: the judge seam ----

    [Fact]
    public async Task The_judged_steps_are_put_back_in_their_places()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        RunCommandSetup setup = NvidiaRun(workspace, RunOrchestrationFixtures.Endpoint());
        var judge = new ScriptedJudge(static steps => [.. steps.Select(static (step, index) => WithQuality(step, index + 1))]);
        UseJudge(setup, judge);

        CliRunResult result = await RunAsync($"run --tier nvidia --task {TaskNames[0]} --repeat 1", setup);

        AssertExit(0, result);
        Assert.Equal(1, judge.Calls);
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        JsonElement[] steps = RunOrchestrationFixtures.Steps(document);
        Assert.Equal(judge.Given, steps.Select(KeyOf));
        Assert.Equal([1.0, 2.0, 3.0, 4.0], steps.Select(static step => step.GetProperty("quality").GetProperty("value").GetDouble()));
        Assert.All(steps, static step => Assert.Equal("model-judged", Text(step.GetProperty("quality"), "label")));
    }

    [Fact]
    public async Task The_judge_is_skipped_when_the_judge_key_is_missing()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        RunCommandSetup setup = RunOrchestrationFixtures.Production(
            workspace.Root,
            new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal) { ["nebius"] = RunOrchestrationFixtures.Endpoint() },
            (ProviderClientFixtures.NvidiaKeyVariable, null));
        var judge = new ScriptedJudge(static steps => steps);
        UseJudge(setup, judge);

        CliRunResult result = await RunAsync($"run --tier nebius --task {TaskNames[0]} --repeat 1", setup);

        AssertExit(0, result);
        Assert.Contains(result.StderrLines, static line => line.StartsWith("Warning: judge key NVIDIA_API_KEY is not set", StringComparison.Ordinal));
        Assert.Equal(0, judge.Calls);
    }

    [Fact]
    public async Task A_judge_that_returns_the_wrong_count_fails_the_tier_with_exit_1()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        RunCommandSetup setup = NvidiaRun(workspace, RunOrchestrationFixtures.Endpoint());
        UseJudge(setup, new ScriptedJudge(static steps => [.. steps.Skip(1)]));

        CliRunResult result = await RunAsync($"run --tier nvidia --task {TaskNames[0]} --repeat 1", setup);

        AssertExit(1, result);
        Assert.EndsWith("(InvalidOperationException)", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        Assert.Equal("running", Text(RunOf(document), "status"));
    }

    // AJ-047: the windows the judge is given come back unchanged, so the completed write keeps the union totals and the shared-window flags.
    [Theory]
    [Trait("Spec", "REQ-013")]
    [InlineData("keep", null)]
    [InlineData("drop", "The judge must return each step with its window unchanged.")]
    [InlineData("null", "The judge must return one step for every step it was given, in the same order.")]
    public async Task A_judge_must_return_every_step_with_its_window_unchanged(string judged, string? error)
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        RunCommandSetup setup = NvidiaRun(workspace, RunOrchestrationFixtures.Endpoint());
        var judge = new ScriptedJudge(steps => [.. steps.Select((step, index) => judged == "null" && index == 0 ? null! : WithQuality(step, index + 1, keepWindow: judged == "keep"))]);
        UseJudge(setup, judge);

        CliRunResult result = await RunAsync($"run --tier nvidia --task {TaskNames[0]} --repeat 1", setup);

        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        if (error is null)
        {
            AssertExit(0, result);
            Assert.Equal("completed", Text(RunOf(document), "status"));
        }
        else
        {
            // The judge dropped the windows or returned no step for one: the tier fails before the completed write, and the message names the rule, never a reading.
            AssertExit(1, result);
            Assert.Equal($"error: {error} (InvalidOperationException)", Assert.Single(result.ErrorLines));
            Assert.Equal("running", Text(RunOf(document), "status"));
        }
    }

    // ---- fix round 1, item 12: the key, the prompt and the answer never reach the console or the file ----

    [Fact]
    public async Task The_key_the_prompt_and_the_answer_never_reach_stdout_stderr_or_the_run_file()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        string key = ProviderClientFixtures.FakeKey();
        string prompt = TaskSetLoader.Load(Path.Combine(ConfigurationFixtures.RepositoryRoot(), RunCommandHarness.TasksFolder))[0].Prompt;
        var nvidia = new FakeHttpMessageHandler
        {
            ModelIds = [.. ConfigurationFixtures.ShippedModelIds(), "echo/" + key, prompt],
            StreamDeltas = [key, " ", prompt],
            ChatText = key + " " + prompt,
        };

        CliRunResult result = await RunAsync($"run --tier nvidia --task {TaskNames[0]} --repeat 1", NvidiaRun(workspace, nvidia));

        AssertExit(0, result);
        string files = string.Concat(Directory.EnumerateFiles(Path.Combine(workspace.Root, "results")).Select(File.ReadAllText));
        foreach ((string name, string text) in new[] { ("stdout", result.Stdout), ("stderr", result.Stderr), ("results", files) })
        {
            Assert.False(text.Contains(key, StringComparison.Ordinal), $"The key reached {name}.");
            Assert.False(text.Contains(prompt[..32], StringComparison.Ordinal), $"The prompt reached {name}.");
        }

        // The answer is kept as its fingerprint only.
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        Assert.All(RunOrchestrationFixtures.Steps(document), step =>
        {
            Assert.Equal(64, Text(step, "outputSha256").Length);
            Assert.Equal(key.Length + 1 + prompt.Length, step.GetProperty("outputChars").GetInt32());
        });
    }

    // ---- fix round 1, item 15: an existing run file is refused before any completion call ----

    [Fact]
    public async Task An_existing_run_file_exits_1_before_any_completion_call_and_is_kept()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        string existing = workspace.WriteFile(Path.Combine("results", "20261021T100000Z-nvidia.json"), "{\"kept\":true}");
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint(time, TimeSpan.FromSeconds(1));
        RunCommandSetup setup = NvidiaRun(workspace, nvidia);
        RunOrchestrationFixtures.UseClock(setup, time);

        CliRunResult result = await RunAsync($"run --tier nvidia --task {TaskNames[0]} --repeat 1", setup);

        AssertExit(1, result);
        Assert.Equal(
            "error: The run file 20261021T100000Z-nvidia.json exists already and is not replaced; wait a second and run again. (AgentJoulesException)",
            Assert.Single(result.ErrorLines));
        Assert.Equal(0, nvidia.RequestCount);
        Assert.Equal("{\"kept\":true}", File.ReadAllText(existing));
    }

    private static void UseJudge(RunCommandSetup setup, IJudgeService judge) =>
        RunOrchestrationFixtures.AddServices(setup, services =>
        {
            _ = services.RemoveAll<IJudgeService>();
            _ = services.AddSingleton(judge);
        });

    // A copy of the reading with a model-judged quality; keepWindow false is a judge that rebuilt the reading and dropped its in-memory window (AJ-047).
    private static StepReading WithQuality(StepReading reading, double quality, bool keepWindow = true) =>
        new(
            reading.Task,
            reading.Repeat,
            reading.Agent,
            reading.ModelId,
            reading.Tier,
            reading.StartUtc,
            reading.EndUtc,
            reading.Outcome,
            reading.LatencyMs,
            reading.TtftMs,
            reading.InputTokens,
            reading.OutputTokens,
            reading.GrossJoules,
            reading.NetJoules,
            reading.AvgW,
            reading.PeakW,
            reading.Cost,
            reading.Retries,
            reading.WaitMs,
            reading.Flags,
            reading.Method,
            reading.SampleCount,
            reading.SampleRateHz,
            reading.ThrottleReasons,
            Metric.ModelJudged(quality),
            qualityReason: null,
            outputSha256: reading.OutputSha256,
            outputChars: reading.OutputChars,
            outputText: reading.OutputText,
            window: keepWindow ? reading.Window : null);

    // A judge double: counts its calls, records the steps it was given and returns what the script makes of them.
    private sealed class ScriptedJudge(Func<IReadOnlyList<StepReading>, IReadOnlyList<StepReading>> score) : IJudgeService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<string> Given { get; private set; } = [];

        public Task<JudgeResult> ScoreAsync(JudgeRequest request, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _calls);
            Given = [.. request.Steps.Select(static step => $"{step.Task}/{step.Repeat}/{step.Agent}")];
            IReadOnlyList<StepReading> scored = score(request.Steps);
            return Task.FromResult(new JudgeResult(scored, SelfJudged: false, JudgedSteps: request.Steps.Count, FailedSteps: 0, FailureKinds: []));
        }
    }

    // Forwards to the console listener and cancels the command token when a tier completes.
    private sealed class CancellingListener(IRunListener inner, CancellationTokenSource cancellation) : IRunListener
    {
        public void StepFinished(StepReading reading) => inner.StepFinished(reading);

        public void BaselineMeasured(BaselineResult baseline) => inner.BaselineMeasured(baseline);

        public void CheckpointFailed(string exceptionTypeName, bool previousCheckpointKept, CheckpointKind kind) => inner.CheckpointFailed(exceptionTypeName, previousCheckpointKept, kind);

        public void TierFailed(string displayPath, string agent, RunStatus status) => inner.TierFailed(displayPath, agent, status);

        public void JudgeFailed(Tier tier, int failedSteps, int judgedSteps, IReadOnlyList<string> failureKinds) => inner.JudgeFailed(tier, failedSteps, judgedSteps, failureKinds);

        public void TierCompleted(string displayPath, Totals totals)
        {
            inner.TierCompleted(displayPath, totals);
            cancellation.Cancel();
        }
    }
}