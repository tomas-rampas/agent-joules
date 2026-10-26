using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Reporting.Json;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Judge;
using AgentJoules.Workflow.Runs;
using AgentJoules.Workflow.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-057 (REQ-017, EDGE-006): a failed step stops the tier, the partial file keeps what finished and exits 5. These tests run the real <c>run</c> command through <see cref="RunCommandHarness"/>
/// with the production orchestrator, the real retry layer and a recording fake endpoint per tier; every retry wait runs on a fake clock, so nothing waits on the wall clock except the one
/// row that needs the SDK network timeout (a real timer of 1 s).
/// </summary>
public sealed partial class RetryPolicyTests
{
    private static readonly string[] FourAgents = ["Planner", "Researcher", "Writer", "Reviewer"];

    private static readonly string[] MetricFields = ["inputTokens", "outputTokens", "grossJoules", "netJoules"];

    private static readonly string[] AbsentLabels = ["not-reported", "N/A"];

    // ---- the criteria through the command ----

    [Fact]
    [Trait("Spec", "REQ-017")]
    public async Task Exhausted_429_writes_partial_file_and_exits_5()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        string key = ProviderClientFixtures.FakeKey();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint(time, CallTime);
        nvidia.SetStatuses(FakeRoute.ChatCompletions, 429);

        // The body of every rejected call echoes the credential: nothing of it may reach the error line, the console or the file.
        nvidia.CustomizeResponse = EchoesTheKey(key);
        RunCommandSetup setup = Failing.Setup(workspace, time, new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia });

        CliRunResult result = await Failing.RunAsync("run --tier nvidia --repeat 1", setup, time);

        Failing.AssertExit(5, result);
        Assert.Equal(5, nvidia.Count(FakeRoute.ChatCompletions));
        string path = Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root));
        string display = "results/" + Path.GetFileName(path);
        using JsonDocument document = RunOrchestrationFixtures.Read(path);
        Assert.Equal("failed", Failing.Text(document.RootElement.GetProperty("run"), "status"));
        JsonElement step = Assert.Single(RunOrchestrationFixtures.Steps(document));
        Assert.Equal(("Planner", "error", 4), (Failing.Text(step, "agent"), Failing.Text(step, "outcome"), step.GetProperty("retries").GetInt32()));

        // The error line is the one stderr line starting with error:, and it names the failing agent and the partial file.
        string error = Assert.Single(result.ErrorLines);
        Assert.StartsWith("error: ", error, StringComparison.Ordinal);
        Assert.EndsWith("(StepFailedException)", error, StringComparison.Ordinal);
        Assert.Contains("Planner", error, StringComparison.Ordinal);
        Assert.Contains("; partial results in " + display, error, StringComparison.Ordinal);

        // The console: the file, then the status line, before the error line.
        string[] lines = RunOrchestrationFixtures.StdoutLines(result);
        int file = Array.IndexOf(lines, "results: " + display);
        Assert.True(file >= 0, $"No results line in: {result.Stdout}");
        Assert.Equal("status: failed at Planner", lines[file + 1]);
        Assert.DoesNotContain(lines, static line => line.StartsWith("totals:", StringComparison.Ordinal));

        Failing.AssertNoProviderText(key, result.Stdout, result.Stderr, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("500")]
    [InlineData("401")]
    [InlineData("refused")]
    [InlineData("timeout")]
    [Trait("Spec", "REQ-017")]
    public async Task A_500_a_refused_connection_and_a_timeout_end_the_tier_with_one_request_status_failed_and_exit_5(string failure)
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        string key = ProviderClientFixtures.FakeKey();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint(time, CallTime);
        HttpMessageHandler endpoint = nvidia;
        HoldingHttpMessageHandler? stalled = null;
        (string Key, string? Value)[] extra = [];
        switch (failure)
        {
            case "500" or "401":
                // The body of the rejected call echoes the credential: nothing of it may reach the console or the file, and the HTTP status stays in the error line.
                nvidia.SetStatuses(FakeRoute.ChatCompletions, int.Parse(failure, CultureInfo.InvariantCulture));
                nvidia.CustomizeResponse = EchoesTheKey(key);
                break;
            case "refused":
                // Only the completion route refuses: the models route of the preflight must still answer. The request is recorded before the hook runs.
                nvidia.CustomizeResponse = static (route, _) =>
                {
                    if (route == FakeRoute.ChatCompletions)
                    {
                        throw new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
                    }
                };
                break;
            default:
                // The first completion request stalls until its token is cancelled; the only thing that cancels it is the SDK network timeout (1 s, a real timer).
                stalled = new HoldingHttpMessageHandler(nvidia, FakeRoute.ChatCompletions, number: 1);
                endpoint = stalled;
                extra = [("AgentJoules:Tiers:nvidia:TimeoutSeconds", "1")];
                break;
        }

        // The timeout row runs on the system clock: the call really lasts a second, and a window of zero length on a fake clock that nothing moved would have no reading.
        TimerTrackingTimeProvider? clock = failure == "timeout" ? null : time;
        RunCommandSetup setup = Failing.Setup(workspace, clock, new Dictionary<string, HttpMessageHandler> { ["nvidia"] = endpoint }, extra);

        CliRunResult result;
        if (stalled is null)
        {
            result = await Failing.RunAsync("run --tier nvidia --repeat 1", setup, clock);
        }
        else
        {
            // A hang must not leak into the later CLI tests through the command gate: the held request outlives the hang guard, so the command token ends it.
            using var cancellation = new CancellationTokenSource();
            CliHarnessRun run = CliHarness.Start("run --tier nvidia --repeat 1", setup.ToOverrides(workspace.Root), cancellationToken: cancellation.Token);
            try
            {
                result = await run.Result.WaitAsync(HangGuard);
            }
            finally
            {
                await Failing.EndAsync(run, cancellation);
            }
        }

        Failing.AssertExit(5, result);

        // One attempt: the stalled request never reaches the fake, and a second attempt would (it is not the held number), so the fake sees none.
        int requests = nvidia.Count(FakeRoute.ChatCompletions) + (stalled is { Reached.IsCompleted: true } ? 1 : 0);
        Assert.Equal(1, requests);
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        Assert.Equal("failed", Failing.Text(document.RootElement.GetProperty("run"), "status"));
        JsonElement step = Assert.Single(RunOrchestrationFixtures.Steps(document));
        Assert.Equal(("Planner", "error", 0), (Failing.Text(step, "agent"), Failing.Text(step, "outcome"), step.GetProperty("retries").GetInt32()));
        string error = Assert.Single(result.ErrorLines);
        Assert.EndsWith("(StepFailedException)", error, StringComparison.Ordinal);
        if (failure is "500" or "401")
        {
            string path = Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root));
            Assert.Contains($"HTTP status {failure}; partial results in results/{Path.GetFileName(path)} (StepFailedException)", error, StringComparison.Ordinal);
            Failing.AssertNoProviderText(key, result.Stdout, result.Stderr, File.ReadAllText(path));
        }
    }

    [Fact]
    [Trait("Spec", "REQ-017")]
    public async Task Steps_finished_before_the_failure_are_kept_unchanged()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint(time, CallTime);

        // Task 1 is four ok calls, task 2 answers its Planner, Researcher and Writer and fails at its Reviewer (the eighth call).
        nvidia.SetStatuses(FakeRoute.ChatCompletions, 200, 200, 200, 200, 200, 200, 200, 500);
        RunCommandSetup setup = Failing.Setup(workspace, time, new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia });
        var orchestrator = new RecordingOrchestrator();
        RunOrchestrationFixtures.AddServices(setup, orchestrator.Register);

        CliRunResult result = await Failing.RunAsync("run --tier nvidia --repeat 1", setup, time);

        Failing.AssertExit(5, result);
        RunDocument[] written = [.. orchestrator.Documents];
        Assert.Equal([RunStatus.Running, RunStatus.Failed], written.Select(static document => document.Run.Status));
        RunDocument running = written[0];
        RunDocument failed = written[1];
        Assert.Equal(4, running.Steps.Count);
        Assert.Equal(8, failed.Steps.Count);

        // The same readings, by value: nothing was rebuilt, rounded or zero-filled. Since AJ-074 Assemble copies each reading to attach the derived metrics, so the contract is value equality, not identity, for these hosted readings (ThrottleReasons null; the generated record equality compares that collection by reference).
        for (int i = 0; i < running.Steps.Count; i++)
        {
            Assert.Equal(running.Steps[i], failed.Steps[i]);
        }

        // On disk: the first four steps of the file are, to the character, the four steps of the checkpoint serialized by the writer's own serializer.
        string path = Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root));
        using JsonDocument file = RunOrchestrationFixtures.Read(path);
        using JsonDocument checkpoint = JsonDocument.Parse(JsonSerializer.Serialize(running, AgentJoulesJsonContext.Default.RunDocument));
        JsonElement[] kept = RunOrchestrationFixtures.Steps(file);
        JsonElement[] before = RunOrchestrationFixtures.Steps(checkpoint);
        Assert.Equal(8, kept.Length);
        Assert.Equal(before.Select(static step => step.GetRawText()), kept.Take(4).Select(static step => step.GetRawText()));

        string[] tasks = Failing.Tasks;
        Assert.Equal(FourAgents, kept.Take(4).Select(static step => Failing.Text(step, "agent")));
        Assert.All(kept.Take(4), step => Assert.Equal(tasks[0], Failing.Text(step, "task")));
        Assert.All(kept.Skip(4), step => Assert.Equal(tasks[1], Failing.Text(step, "task")));
        Assert.Equal(7, kept.Count(static step => Failing.Text(step, "outcome") == "ok"));
        Assert.Equal(("Reviewer", "error"), (Failing.Text(kept[7], "agent"), Failing.Text(kept[7], "outcome")));
        Assert.Equal("failed", Failing.Text(file.RootElement.GetProperty("run"), "status"));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [Trait("Spec", "REQ-017")]
    public async Task A_first_step_failure_writes_one_step_and_no_numeric_zero(int status)
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint(time, CallTime);
        nvidia.SetStatuses(FakeRoute.ChatCompletions, status);
        RunCommandSetup setup = Failing.Setup(workspace, time, new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia });

        CliRunResult result = await Failing.RunAsync("run --tier nvidia --repeat 1", setup, time);

        Failing.AssertExit(5, result);
        using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
        JsonElement step = Assert.Single(RunOrchestrationFixtures.Steps(document));
        Assert.Equal("error", Failing.Text(step, "outcome"));

        // A step that never reported a count or an energy carries a label, never a number: the tokens are not reported and the hosted energy is not applicable.
        foreach (string field in MetricFields)
        {
            JsonElement metric = step.GetProperty(field);
            Assert.True(metric.ValueKind != JsonValueKind.Number, $"{field} is the number {metric.GetRawText()}.");
            Assert.Equal(JsonValueKind.Object, metric.ValueKind);
            Assert.Contains(Failing.Text(metric, "label"), AbsentLabels);
        }
    }

    [Fact]
    [Trait("Spec", "REQ-017")]
    public async Task Tier_all_stops_at_the_first_failing_tier_and_keeps_earlier_files()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint(time, CallTime);
        FakeHttpMessageHandler nebius = RunOrchestrationFixtures.Endpoint(time, CallTime);
        nebius.SetStatuses(FakeRoute.ChatCompletions, 429);
        RunCommandSetup setup = Failing.Setup(
            workspace,
            time,
            new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia, ["nebius"] = nebius },
            ("AgentJoules:Tiers:local:Enabled", "false"));

        CliRunResult result = await Failing.RunAsync("run --tier all --repeat 1", setup, time);

        Failing.AssertExit(5, result);
        string[] files = RunOrchestrationFixtures.RunFilesIn(workspace.Root);
        Assert.Equal(2, files.Length);
        Assert.DoesNotContain(files, static path => path.EndsWith("-local.json", StringComparison.Ordinal));
        using JsonDocument first = RunOrchestrationFixtures.Read(Assert.Single(files, static path => path.EndsWith("-nvidia.json", StringComparison.Ordinal)));
        using JsonDocument second = RunOrchestrationFixtures.Read(Assert.Single(files, static path => path.EndsWith("-nebius.json", StringComparison.Ordinal)));
        Assert.Equal("completed", Failing.Text(first.RootElement.GetProperty("run"), "status"));
        Assert.Equal(12, RunOrchestrationFixtures.Steps(first).Length);
        Assert.Equal("failed", Failing.Text(second.RootElement.GetProperty("run"), "status"));
        Assert.Equal("error", Failing.Text(Assert.Single(RunOrchestrationFixtures.Steps(second)), "outcome"));
        Assert.Equal(5, nebius.Count(FakeRoute.ChatCompletions));
        Assert.EndsWith("(StepFailedException)", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-017")]
    public async Task Tier_all_does_not_start_a_later_tier_after_a_failure()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint(time, CallTime);
        FakeHttpMessageHandler nebius = RunOrchestrationFixtures.Endpoint(time, CallTime);
        nvidia.SetStatuses(FakeRoute.ChatCompletions, 500);
        RunCommandSetup setup = Failing.Setup(
            workspace,
            time,
            new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia, ["nebius"] = nebius },
            ("AgentJoules:Tiers:local:Enabled", "false"));

        CliRunResult result = await Failing.RunAsync("run --tier all --repeat 1", setup, time);

        Failing.AssertExit(5, result);
        string path = Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root));
        Assert.EndsWith("-nvidia.json", path, StringComparison.Ordinal);
        Assert.Equal(1, nvidia.Count(FakeRoute.ChatCompletions));
        Assert.Equal(0, nebius.Count(FakeRoute.ChatCompletions));
    }

    // ---- a failed write of the partial file ----

    // Failure on the first task: the failed write is the tier's first and nothing is on disk. Failure on the second task: the running checkpoint of the first task stays, whole.
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [Trait("Spec", "REQ-017")]
    public async Task A_failed_checkpoint_write_warns_and_still_exits_5(int failingTask, bool checkpointKept)
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint(time, CallTime);
        nvidia.SetStatuses(FakeRoute.ChatCompletions, failingTask == 1 ? [500] : [200, 200, 200, 200, 500]);
        RunCommandSetup setup = Failing.Setup(workspace, time, new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia });
        var orchestrator = new RecordingOrchestrator(static document => document.Run.Status == RunStatus.Failed ? new IOException("disk-detail-9c1e") : null);
        RunOrchestrationFixtures.AddServices(setup, orchestrator.Register);

        CliRunResult result = await Failing.RunAsync("run --tier nvidia --repeat 1", setup, time);

        Failing.AssertExit(5, result);
        string ending = checkpointKept ? "the last complete checkpoint was kept" : "no checkpoint of this tier was written";
        _ = Assert.Single(result.StderrLines, line => line == $"Warning: the run file could not be written after the failed step (IOException); {ending}");
        Assert.DoesNotContain("disk-detail-9c1e", result.Stderr, StringComparison.Ordinal);

        // The error line makes no claim about a partial file that was not written, and no results or status line is printed.
        string error = Assert.Single(result.ErrorLines);
        Assert.EndsWith("(StepFailedException)", error, StringComparison.Ordinal);
        Assert.DoesNotContain("partial results", error, StringComparison.Ordinal);
        Assert.DoesNotContain(RunOrchestrationFixtures.StdoutLines(result), static line => line.StartsWith("results:", StringComparison.Ordinal) || line.StartsWith("status:", StringComparison.Ordinal));
        Assert.Equal(RunStatus.Failed, orchestrator.Writes[^1].Status);

        if (checkpointKept)
        {
            using JsonDocument document = RunOrchestrationFixtures.Read(Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root)));
            Assert.Equal("running", Failing.Text(document.RootElement.GetProperty("run"), "status"));
            Assert.Equal(4, RunOrchestrationFixtures.Steps(document).Length);
        }
        else
        {
            Assert.Equal([RunStatus.Failed], orchestrator.Writes.Select(static write => write.Status));
            Assert.Empty(RunOrchestrationFixtures.EntriesIn(workspace.Root));
        }
    }

    // ---- a connection that drops after the first update ----

    [Fact]
    [Trait("Spec", "REQ-017")]
    public async Task An_incomplete_failing_step_writes_status_incomplete()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint(time, CallTime);

        // The stream delivers its role chunk and one text chunk, then the connection drops: the failure came after the first update (EDGE-016).
        nvidia.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.StreamFailure(1));
        RunCommandSetup setup = Failing.Setup(workspace, time, new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia });

        CliRunResult result = await Failing.RunAsync("run --tier nvidia --repeat 1", setup, time);

        Failing.AssertExit(5, result);
        string path = Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root));
        using JsonDocument document = RunOrchestrationFixtures.Read(path);
        Assert.Equal("incomplete", Failing.Text(document.RootElement.GetProperty("run"), "status"));
        JsonElement step = Assert.Single(RunOrchestrationFixtures.Steps(document));
        Assert.Equal(("Planner", "incomplete"), (Failing.Text(step, "agent"), Failing.Text(step, "outcome")));
        string[] lines = RunOrchestrationFixtures.StdoutLines(result);
        int file = Array.IndexOf(lines, "results: results/" + Path.GetFileName(path));
        Assert.True(file >= 0, $"No results line in: {result.Stdout}");
        Assert.Equal("status: incomplete at Planner", lines[file + 1]);
        Assert.EndsWith("(StepFailedException)", Assert.Single(result.ErrorLines), StringComparison.Ordinal);
    }

    // ---- a console that cannot be written, and a judge that must not run ----

    [Fact]
    [Trait("Spec", "REQ-017")]
    public async Task A_listener_that_fails_on_the_failed_tier_changes_neither_the_file_nor_the_exit_code()
    {
        using TempWorkspace workspace = RunCommandHarness.CreateWorkspace();
        TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
        FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint(time, CallTime);
        nvidia.SetStatuses(FakeRoute.ChatCompletions, 500);
        RunCommandSetup setup = Failing.Setup(workspace, time, new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia });
        RunOrchestrationFixtures.AddServices(setup, static services =>
        {
            ServiceDescriptor original = Assert.Single(services, static descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IRunListener));
            _ = services.AddSingleton<IRunListener>(provider => new Failing.TierFailedThrows((IRunListener)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!)));
        });

        CliRunResult result = await Failing.RunAsync("run --tier nvidia --repeat 1", setup, time);

        Failing.AssertExit(5, result);
        string path = Assert.Single(RunOrchestrationFixtures.RunFilesIn(workspace.Root));
        using JsonDocument document = RunOrchestrationFixtures.Read(path);
        Assert.Equal("failed", Failing.Text(document.RootElement.GetProperty("run"), "status"));
        Assert.Equal("error", Failing.Text(Assert.Single(RunOrchestrationFixtures.Steps(document)), "outcome"));

        // The copy that names the file still leaves, and nothing of the console failure reaches the user.
        string error = Assert.Single(result.ErrorLines);
        Assert.Contains("; partial results in results/" + Path.GetFileName(path) + " (StepFailedException)", error, StringComparison.Ordinal);
        Assert.DoesNotContain("console-detail-3b7e", result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Spec", "REQ-017")]
    public async Task The_judge_is_not_called_for_a_failed_tier_and_is_called_once_for_a_completed_one()
    {
        var judge = new Failing.CountingJudge();

        // Failed: the tier stops at its first step.
        using (TempWorkspace failedWorkspace = RunCommandHarness.CreateWorkspace())
        {
            TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
            FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint(time, CallTime);

            // Task 1 completes (four ok steps the judge could score), task 2 fails at its Planner.
            nvidia.SetStatuses(FakeRoute.ChatCompletions, 200, 200, 200, 200, 500);
            RunCommandSetup setup = Failing.Setup(failedWorkspace, time, new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia });
            Failing.UseJudge(setup, judge);

            CliRunResult result = await Failing.RunAsync("run --tier nvidia --repeat 1", setup, time);

            Failing.AssertExit(5, result);
            Assert.Equal(0, judge.Calls);
        }

        // Completed: the positive control, the same judge is reached through the same registration.
        using (TempWorkspace completedWorkspace = RunCommandHarness.CreateWorkspace())
        {
            TimerTrackingTimeProvider time = RunOrchestrationFixtures.Clock();
            FakeHttpMessageHandler nvidia = RunOrchestrationFixtures.Endpoint(time, CallTime);
            RunCommandSetup setup = Failing.Setup(completedWorkspace, time, new Dictionary<string, HttpMessageHandler> { ["nvidia"] = nvidia });
            Failing.UseJudge(setup, judge);

            CliRunResult result = await Failing.RunAsync("run --tier nvidia --repeat 1", setup, time);

            Failing.AssertExit(0, result);
            Assert.Equal(1, judge.Calls);
        }
    }

    // ---- the exception ----

    [Fact]
    [Trait("Spec", "REQ-017")]
    public void A_step_failure_names_no_partial_file_until_it_is_given_one()
    {
        var failure = new StepFailedException("Planner", "RetriesExhaustedException", 429);

        Assert.Null(failure.PartialFile);
        Assert.Equal("Step Planner failed with RetriesExhaustedException and HTTP status 429", failure.Message);
    }

    [Fact]
    [Trait("Spec", "REQ-017")]
    public void WithPartialFile_copies_the_failure_and_names_the_file_in_the_message()
    {
        var inner = new IOException("inner");
        var original = new StepFailedException("Reviewer", "ClientResultException", 500, inner);

        StepFailedException copy = original.WithPartialFile("results/20261021T100000Z-nvidia.json");

        Assert.NotSame(original, copy);
        Assert.Equal(("Reviewer", "ClientResultException", 500), (copy.Agent, copy.FailureType, copy.StatusCode));
        Assert.Same(inner, copy.InnerException);
        Assert.Equal(ExitCode.Unreachable, copy.ExitCode);
        Assert.Equal("results/20261021T100000Z-nvidia.json", copy.PartialFile);
        Assert.Equal("Step Reviewer failed with ClientResultException and HTTP status 500; partial results in results/20261021T100000Z-nvidia.json", copy.Message);

        // The original is untouched, and a failure without an attached exception copies without one.
        Assert.Null(original.PartialFile);
        Assert.Equal("Step Reviewer failed with ClientResultException and HTTP status 500", original.Message);
        StepFailedException bare = new StepFailedException("Writer", "TimeoutException").WithPartialFile("out/x.json");
        Assert.Null(bare.InnerException);
        Assert.Null(bare.StatusCode);
        Assert.Equal("Step Writer failed with TimeoutException; partial results in out/x.json", bare.Message);
    }

    [Fact]
    [Trait("Spec", "REQ-017")]
    public void The_partial_file_in_the_message_is_cut_and_scrubbed()
    {
        var original = new StepFailedException("Planner", "TimeoutException");

        StepFailedException cut = original.WithPartialFile("out/" + new string('a', 3000));
        StepFailedException scrubbed = original.WithPartialFile("out/line\nbreak" + (char)0x202E + "tail");

        // The file is kept in full; only the echo in the message is bounded.
        Assert.Equal(4 + 3000, cut.PartialFile!.Length);
        Assert.EndsWith("...", cut.Message, StringComparison.Ordinal);
        Assert.InRange(cut.Message.Length, 1, 1200);
        Assert.EndsWith("; partial results in out/line?break?tail", scrubbed.Message, StringComparison.Ordinal);
        _ = Assert.Throws<ArgumentException>(() => original.WithPartialFile(" "));
        _ = Assert.Throws<ArgumentNullException>(() => original.WithPartialFile(null!));
    }

    // The members of this file that more than one test needs.
    private static class Failing
    {
        public static void UseJudge(RunCommandSetup setup, IJudgeService judge) =>
            RunOrchestrationFixtures.AddServices(setup, services =>
            {
                _ = services.RemoveAll<IJudgeService>();
                _ = services.AddSingleton(judge);
            });

        // Counts its calls and returns the steps it was given unchanged.
        public sealed class CountingJudge : IJudgeService
        {
            private int _calls;

            public int Calls => Volatile.Read(ref _calls);

            public Task<JudgeResult> ScoreAsync(JudgeRequest request, CancellationToken cancellationToken)
            {
                _ = Interlocked.Increment(ref _calls);
                return Task.FromResult(new JudgeResult(request.Steps, SelfJudged: false, JudgedSteps: 0, FailedSteps: 0, FailureKinds: []));
            }
        }

        // The console listener of the command, except that it fails when told that a tier failed.
        public sealed class TierFailedThrows(IRunListener inner) : IRunListener
        {
            public void StepFinished(StepReading reading) => inner.StepFinished(reading);

            public void CheckpointFailed(string exceptionTypeName, bool previousCheckpointKept, CheckpointKind kind) => inner.CheckpointFailed(exceptionTypeName, previousCheckpointKept, kind);

            public void BaselineMeasured(BaselineResult baseline) => inner.BaselineMeasured(baseline);

            public void SettleStarted(double seconds) => inner.SettleStarted(seconds);

            public void TierCompleted(string displayPath, Totals totals) => inner.TierCompleted(displayPath, totals);

            public void JudgeFailed(Tier tier, int failedSteps, int judgedSteps, IReadOnlyList<string> failureKinds) => inner.JudgeFailed(tier, failedSteps, judgedSteps, failureKinds);

            public void TierFailed(string displayPath, string agent, RunStatus status) => throw new IOException("console-detail-3b7e");

            public void BudgetStopDecided(string reason) => inner.BudgetStopDecided(reason);
        }

        public static string[] Tasks => [.. TaskSetLoader.Load(Path.Combine(ConfigurationFixtures.RepositoryRoot(), RunCommandHarness.TasksFolder)).Select(static task => task.Name)];

        // A null clock is the system clock.
        public static RunCommandSetup Setup(TempWorkspace workspace, TimerTrackingTimeProvider? time, IReadOnlyDictionary<string, HttpMessageHandler> endpoints, params (string Key, string? Value)[] extra)
        {
            RunCommandSetup setup = RunOrchestrationFixtures.Production(workspace.Root, endpoints, extra);
            if (time is not null)
            {
                RunOrchestrationFixtures.UseClock(setup, time);
            }

            return setup;
        }

        public static Task<CliRunResult> RunAsync(string arguments, RunCommandSetup setup, TimerTrackingTimeProvider? time) =>
            (time is null ? RunCommandHarness.RunAsync(arguments, setup) : RunOrchestrationFixtures.RunDrivenAsync(arguments, setup, time)).WaitAsync(HangGuard);

        public static void AssertExit(int expected, CliRunResult result) =>
            Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stderr: {result.Stderr}");

        // Cancels the command token and waits for the command to end, so the command gate is free for the next test.
        public static async Task EndAsync(CliHarnessRun run, CancellationTokenSource cancellation)
        {
            await cancellation.CancelAsync();
            try
            {
                _ = await run.Result.WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (TimeoutException)
            {
                // The assertion that failed first is the one the test reports.
            }
        }

        public static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;

        // No part of the response body that echoed the credential: no 6-character window of the key (a whole-key check would miss a truncated or masked echo) and not the sentence around it.
        public static void AssertNoProviderText(string key, params string[] texts)
        {
            foreach (string text in texts)
            {
                for (int start = 0; start + 6 <= key.Length; start++)
                {
                    Assert.DoesNotContain(key.Substring(start, 6), text, StringComparison.Ordinal);
                }

                Assert.DoesNotContain("Incorrect", text, StringComparison.Ordinal);
                Assert.DoesNotContain("fake.test", text, StringComparison.Ordinal);
            }
        }
    }
}