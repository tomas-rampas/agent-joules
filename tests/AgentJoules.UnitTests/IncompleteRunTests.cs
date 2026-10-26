using System.Text.Json;

using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.Reporting.Results;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Judge;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-123 (EDGE-016): a cloudgpu run cut off by a lost connection is kept on disk as <c>incomplete</c> and never becomes a result. The run tests run the real <c>run --tier cloudgpu</c> command on a fake clock against a
/// fake cloudgpu endpoint that drops the stream of the second chat completion; the selection tests run <see cref="RunFileSelector"/> on header stubs.
/// </summary>
[Trait("Spec", "EDGE-016")]
public sealed class IncompleteRunTests
{
    private const string Command = "run --tier cloudgpu --repeat 1 --task incident-brief";

    private static string Stub(string status) =>
        $"{{\"schemaVersion\":1,\"run\":{{\"id\":\"r\",\"tier\":\"cloudgpu\",\"status\":\"{status}\",\"flags\":[]}}}}";

    // The workflow fans out after the Planner: the second completion (one of the two siblings) drops its stream after one delta; the serialized gate refuses the other sibling, and a third and a fourth answer exist so that the sibling, the Reviewer or a retry would be counted.
    private static CloudGpuBudgetRig DroppingRig(bool ampleBudget = true, int responseDelayMs = 0)
    {
        CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create(TimeSpan.FromMilliseconds(responseDelayMs));
        rig.Cloud.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.Ok(), FakeAnswer.StreamFailure(1), FakeAnswer.Ok(), FakeAnswer.Ok());
        if (ampleBudget)
        {
            rig.SetObservedCost(1.0);
            rig.SetTierStart(CloudGpuBudgetRig.ClockStart);
        }

        return rig;
    }

    private static void AssertExit(int expected, CliRunResult result) =>
        Assert.True(expected == result.ExitCode, $"Expected exit code {expected} but got {result.ExitCode}; stdout: {result.Stdout}; stderr: {result.Stderr}");

    // The second row answers every request 50 ms late in real time (the handler's ResponseDelay; the rig's fake clock cannot model it): the calls of the siblings then overlap as on a network, and the sibling's
    // refusal can be reported before the dropping call has published its reading (gatekeeper B-1: 7 of 10 runs named the Writer's refusal at 50 ms before fix round 1).
    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    public async Task A_drop_on_the_second_agent_call_yields_an_incomplete_step_and_an_incomplete_file_that_is_kept(int responseDelayMs)
    {
        using CloudGpuBudgetRig rig = DroppingRig(responseDelayMs: responseDelayMs);

        CliRunResult result = await rig.RunAsync(Command);

        AssertExit(5, result);
        using JsonDocument document = rig.ReadRunFile();
        JsonElement[] steps = [.. document.RootElement.GetProperty("steps").EnumerateArray()];
        Assert.Equal(2, steps.Length);
        Assert.Equal(StepOutcomes.Ok, steps[0].GetProperty("outcome").GetString());
        Assert.Equal(StepOutcomes.Incomplete, steps[1].GetProperty("outcome").GetString());
        Assert.Equal("incomplete", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        Assert.Contains("Check the VM state in the console", result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sibling_queued_behind_the_dropped_call_is_refused_and_the_run_is_incomplete_whichever_failure_is_reported_first()
    {
        using CloudGpuBudgetRig rig = DroppingRig();

        // The stream of the dropped call is held open until the sibling waits for the gate, and the releaser of the gate stays busy for 100 ms (real time) after it gave the slot on: the sibling is handed the slot, is refused
        // and fails before the dropped call has published its reading, so its failure is reported first. A gate that is stopped after it was released would admit the sibling in those 100 ms and a third request would be sent.
        QueueObservingMeterGate gate = rig.HoldDropUntilSiblingQueues(TimeSpan.FromMilliseconds(100));

        CliRunResult result = await rig.RunAsync(Command);

        AssertExit(5, result);
        Assert.True(gate.WaiterQueued.IsCompleted, "The sibling never waited for the gate: the test did not exercise the queued waiter.");
        Assert.Equal(2, rig.Completions);
        using JsonDocument document = rig.ReadRunFile();
        Assert.Equal("incomplete", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        JsonElement[] steps = [.. document.RootElement.GetProperty("steps").EnumerateArray()];
        JsonElement dropped = Assert.Single(steps, static step => step.GetProperty("outcome").GetString() == StepOutcomes.Incomplete);
        string output = result.Stdout + result.Stderr;
        Assert.Contains("Check the VM state in the console", output, StringComparison.Ordinal);
        Assert.Contains("Step " + dropped.GetProperty("agent").GetString() + " failed", output, StringComparison.Ordinal);
        Assert.DoesNotContain("IncompleteRunException", output, StringComparison.Ordinal);
    }

    // Two different failures in one concurrent repeat (gatekeeper round 2, M-1): one sibling's stream is cut after the first update and the other is answered with HTTP 400. The cut is held back 400 ms (real time), so the 400 is
    // reported first. Which sibling gets which answer depends on who asks first, so the test reads the agent of the step that ended in error and requires the line to name that agent, the type and the status of the 400.
    private static void AssertNamesTheHttpError(CliRunResult result, JsonDocument document)
    {
        JsonElement[] steps = [.. document.RootElement.GetProperty("steps").EnumerateArray()];
        string agent = Assert.Single(steps, static step => step.GetProperty("outcome").GetString() == StepOutcomes.Error).GetProperty("agent").GetString()!;
        string output = result.Stdout + result.Stderr;
        Assert.Contains($"Step {agent} failed with ClientResultException and HTTP status 400", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Check the VM state", output, StringComparison.Ordinal);
        Assert.DoesNotContain("IncompleteRunException", output, StringComparison.Ordinal);
    }

    // The cut stream is held until 400 ms (real time) after the third chat completion arrived (the 400): a delay that starts with the test would have run out during the setup of a loaded machine.
    private static void HoldCutUntilAfterThirdCompletion(FakeHttpMessageHandler endpoint)
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int completions = 0;
        endpoint.CustomizeResponse = (route, response) =>
        {
            if (route == FakeRoute.ChatCompletions && Interlocked.Increment(ref completions) == 3)
            {
                _ = Task.Delay(TimeSpan.FromMilliseconds(400)).ContinueWith(static (_, state) => ((TaskCompletionSource)state!).TrySetResult(), released, TaskScheduler.Default);
            }
        };
        endpoint.HoldDropUntil = released.Task;
    }

    [Fact]
    public async Task On_nebius_a_concurrent_repeat_with_a_cut_stream_and_an_HTTP_400_names_the_call_that_got_the_400_and_is_a_failed_file()
    {
        using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
        FakeHttpMessageHandler nebius = rig.UseNebius();
        nebius.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.Ok(), FakeAnswer.StreamFailure(1), new FakeAnswer(400), FakeAnswer.Ok());
        HoldCutUntilAfterThirdCompletion(nebius);

        CliRunResult result = await rig.RunAsync("run --tier nebius --mode concurrent --repeat 1 --task incident-brief");

        AssertExit(5, result);
        using JsonDocument document = rig.ReadRunFile();
        Assert.Equal("failed", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        AssertNamesTheHttpError(result, document);
    }

    // On cloudgpu the 400 is a non-ok outcome that halts the gate and the cut is an incomplete reading; the run is decided by the failure that is reported, which is the real error of the call that got the 400: a failed file
    // and exit 5, without the connection-lost line.
    [Fact]
    public async Task On_cloudgpu_a_concurrent_repeat_with_a_cut_stream_and_an_HTTP_400_names_the_call_that_got_the_400_and_is_a_failed_file()
    {
        using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
        rig.Cloud.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.Ok(), FakeAnswer.StreamFailure(1), new FakeAnswer(400), FakeAnswer.Ok());
        HoldCutUntilAfterThirdCompletion(rig.Cloud);
        rig.SetObservedCost(1.0);
        rig.SetTierStart(CloudGpuBudgetRig.ClockStart);

        CliRunResult result = await rig.RunAsync(Command + " --mode concurrent");

        AssertExit(5, result);
        using JsonDocument document = rig.ReadRunFile();
        Assert.Equal("failed", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        AssertNamesTheHttpError(result, document);
    }

    // The wrap of the gate is per tier (m-2): on nebius a call that is cut stops nothing, so the sibling that waited behind it still sends its request (three completions, as before AJ-123).
    [Fact]
    public async Task On_nebius_a_cut_stream_does_not_stop_the_gate_so_the_queued_sibling_still_sends_its_request()
    {
        using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
        FakeHttpMessageHandler nebius = rig.UseNebius();
        nebius.SetAnswers(FakeRoute.ChatCompletions, FakeAnswer.Ok(), FakeAnswer.StreamFailure(1), FakeAnswer.Ok(), FakeAnswer.Ok());
        QueueObservingMeterGate gate = rig.HoldDropUntilSiblingQueues(TimeSpan.FromMilliseconds(100), nebius);

        CliRunResult result = await rig.RunAsync("run --tier nebius --mode serialized --repeat 1 --task incident-brief");

        AssertExit(5, result);
        Assert.True(gate.WaiterQueued.IsCompleted, "The sibling never waited for the gate: the test did not exercise the queued waiter.");
        Assert.Equal(3, nebius.Count(FakeRoute.ChatCompletions));
    }

    [Fact]
    public async Task A_client_timeout_stops_the_gate_as_a_drop_does_and_stays_a_failed_file_without_the_connection_lost_line()
    {
        using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
        int completions = 0;

        // What the HttpClient does when the timeout of the tier passes: a TaskCanceledException whose inner exception is a TimeoutException, on a caller token that is not cancelled (an error, not a cancellation).
        rig.Cloud.CustomizeResponse = (route, _) =>
        {
            if (route == FakeRoute.ChatCompletions && Interlocked.Increment(ref completions) == 2)
            {
                throw new TaskCanceledException("timeout", new TimeoutException());
            }
        };

        CliRunResult result = await rig.RunAsync(Command);

        AssertExit(5, result);
        Assert.Equal(2, rig.Completions);
        using JsonDocument document = rig.ReadRunFile();
        Assert.Equal("failed", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        Assert.DoesNotContain("Check the VM state", result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_judge_gets_the_plain_gate_of_the_mode_so_a_dropped_judge_call_cannot_stop_the_workflow_gate()
    {
        using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
        var judge = new CapturingJudge();
        rig.UseJudge(judge);

        CliRunResult result = await rig.RunAsync(Command);

        AssertExit(0, result);
        MeteringServices services = Assert.Single(judge.Requests).Services;
        Assert.IsNotType<IStoppableMeterGate>(services.Gate, exactMatch: false);
    }

    private sealed class CapturingJudge : IJudgeService
    {
        private readonly List<JudgeRequest> _requests = [];

        public IReadOnlyList<JudgeRequest> Requests => _requests;

        public Task<JudgeResult> ScoreAsync(JudgeRequest request, CancellationToken cancellationToken)
        {
            _requests.Add(request);
            return new NoOpJudgeService().ScoreAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task The_output_asks_the_user_to_check_the_VM_state_in_the_console()
    {
        using CloudGpuBudgetRig rig = DroppingRig();

        CliRunResult result = await rig.RunAsync(Command);

        AssertExit(5, result);
        Assert.Contains("Check the VM state in the console", result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoRetryAfterDrop()
    {
        using CloudGpuBudgetRig rig = DroppingRig();

        CliRunResult result = await rig.RunAsync(Command);

        // Time and cost allow more: the guard would say Continue, and two more answers are scripted. Only the Planner and the dropped call were asked for: the serialized gate had not admitted the other
        // sibling when the stream dropped, and it is refused, so neither it, the Reviewer nor a retry sends a request.
        AssertExit(5, result);
        Assert.Equal(2, rig.Completions);
        RecordedDrop drop = Assert.Single(rig.Cloud.Drops);
        Assert.DoesNotContain(rig.Cloud.Requests, request => request.Order > drop.Order);
        Assert.DoesNotContain(rig.Cloud.Requests, request => request.StartedAt > drop.At);
    }

    [Fact]
    public async Task A_stopped_gate_refuses_a_new_caller_and_a_caller_that_was_already_queued()
    {
        using var inner = new SerializedMeterGate();
        var gate = new StopAfterDropMeterGate(inner);
        IAsyncDisposable holder = await gate.AcquireAsync(CancellationToken.None);
        ValueTask<IAsyncDisposable> queued = gate.AcquireAsync(CancellationToken.None);

        Assert.IsType<IStoppableMeterGate>(gate, exactMatch: false).Halt();
        await holder.DisposeAsync();

        _ = await Assert.ThrowsAsync<IncompleteRunException>(async () => await queued);
        _ = await Assert.ThrowsAsync<IncompleteRunException>(async () => await gate.AcquireAsync(CancellationToken.None));
        Assert.Equal(1, inner.CurrentCount);
    }

    [Fact]
    public async Task Rate_limit_exhaustion_stays_an_error_and_a_failed_file()
    {
        using CloudGpuBudgetRig rig = CloudGpuBudgetRig.Create();
        rig.Cloud.SetStatuses(FakeRoute.ChatCompletions, 429, 429, 429, 429, 429);

        CliRunResult result = await rig.RunAsync(Command);

        AssertExit(5, result);
        using JsonDocument document = rig.ReadRunFile();
        JsonElement last = document.RootElement.GetProperty("steps").EnumerateArray().Last();
        Assert.Equal(StepOutcomes.Error, last.GetProperty("outcome").GetString());
        Assert.Equal("failed", document.RootElement.GetProperty("run").GetProperty("status").GetString());
        Assert.DoesNotContain("Check the VM state", result.Stdout + result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Latest_returns_a_completed_file_over_an_older_incomplete_one()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(Path.Combine("results", "20261023T090000Z-cloudgpu.json"), Stub("incomplete"));
        string completed = workspace.WriteFile(Path.Combine("results", "20261024T090000Z-cloudgpu.json"), Stub("completed"));

        Assert.Equal(completed, RunFileSelector.Latest(Path.Combine(workspace.Root, "results"), Tier.CloudGpu));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("running")]
    [InlineData("failed")]
    public void Latest_skips_a_newer_file_that_is_not_a_result(string status)
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string completed = workspace.WriteFile(Path.Combine("results", "20261023T090000Z-cloudgpu.json"), Stub("completed"));
        _ = workspace.WriteFile(Path.Combine("results", "20261024T090000Z-cloudgpu.json"), Stub(status));

        Assert.Equal(completed, RunFileSelector.Latest(Path.Combine(workspace.Root, "results"), Tier.CloudGpu));
    }

    [Fact]
    public void A_copied_file_left_as_running_is_skipped_and_nothing_is_returned()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        _ = workspace.WriteFile(Path.Combine("results", "20261024T090000Z-cloudgpu.json"), Stub("running"));

        Assert.Null(RunFileSelector.Latest(Path.Combine(workspace.Root, "results"), Tier.CloudGpu));
    }

    [Fact]
    public void A_partial_file_is_selectable()
    {
        using TempWorkspace workspace = TempWorkspace.CreateEmpty();
        string partial = workspace.WriteFile(Path.Combine("results", "20261024T090000Z-cloudgpu.json"), Stub("partial"));

        Assert.Equal(partial, RunFileSelector.Latest(Path.Combine(workspace.Root, "results"), Tier.CloudGpu));
    }
}