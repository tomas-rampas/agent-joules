using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;

using AgentJoules.Cli.Output;
using AgentJoules.Core.Cli;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Tasks;
using AgentJoules.Metering;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Graph;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentJoules.UnitTests;

public sealed partial class AttributionBaselineTests
{
    /// <summary>
    /// The REQ-011 cases of AJ-050, in the class that the story names (<c>AttributionBaselineTests</c>) but in a nested class of their own: xUnit takes traits from the
    /// test class and the method, not from the enclosing class, so the class-level <c>REQ-005</c> trait of the M1 tests does not tag these.
    /// </summary>
    public sealed class WorkflowCases
    {
        // ----- REQ-011 (AJ-050): Planner, fan-out to Researcher and Writer, fan-in barrier, Reviewer. The clients are fakes on one fake clock (or the real SDK against a fake
        // endpoint); the clock moves only when a fake yields or the code under test waits, so nothing waits on the wall clock and no request leaves the process.

        private const char Bell = (char)7;
        private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
        private static readonly string[] AgentNames = ["Planner", "Researcher", "Writer", "Reviewer"];

        private static StepReading ReadingOf(WorkflowRig rig, string agent) => Assert.Single(rig.ReadingsOf(agent));

        private static string UserTextOf(FakeChatCall call) => Assert.Single(call.Messages, message => message.Role == ChatRole.User).Text;

        private static TimeSpan? DelayOf(string agent) => agent switch
        {
            "Researcher" => TimeSpan.FromSeconds(1),
            "Writer" => TimeSpan.FromSeconds(2),
            _ => null,
        };

        private static Func<string, TimerTrackingTimeProvider, IChatClient> FailingClient(string failing, Exception failure) =>
            (agent, time) => agent == failing
                ? new FakeChatClient(time) { Updates = [new FakeUpdate("never yielded")], Failure = failure, FailAfterUpdates = 0 }
                : WorkflowRig.StreamingClient(time, agent);

        private static async Task<FinalOutput> RunWithFactoryAsync(WorkflowRig rig, AgentJoulesWorkflowFactory factory)
        {
            Task<FinalOutput> run = factory.RunAsync(CancellationToken.None);
            await FakeTimeDriver.RunWaitsToCompletionAsync(run, rig.Time);
            return await run;
        }

        private static Type ExecutorType(string name) =>
            typeof(AgentJoulesWorkflowFactory).Assembly.GetType("AgentJoules.Workflow.Graph." + name, throwOnError: true)!;

        private static Type[] Declared<TAttribute>(Type executor, Func<TAttribute, Type> selector)
            where TAttribute : Attribute =>
            [.. executor.GetCustomAttributes<TAttribute>(inherit: false).Select(selector)];

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task Workflow_has_one_reading_per_agent()
        {
            await using WorkflowRig rig = await WorkflowRig.CreateAsync();

            FinalOutput output = await rig.RunAsync();

            IReadOnlyList<StepReading> readings = rig.Readings();
            Assert.Equal(4, readings.Count);
            Assert.Equal(AgentNames.Order(StringComparer.Ordinal), readings.Select(reading => reading.Agent).Order(StringComparer.Ordinal));

            foreach (string agent in AgentNames)
            {
                // Each reading names the agent whose client produced it: the fake that served the call recorded the model of its agent.
                StepReading reading = ReadingOf(rig, agent);
                FakeChatCall call = Assert.Single(rig.FakeOf(agent).Calls);
                Assert.Equal(WorkflowRig.ModelOf(agent), reading.ModelId);
                Assert.Equal(reading.ModelId, call.Options?.ModelId);
                Assert.Equal(StepOutcomes.Ok, reading.Outcome);
                Assert.Equal(WorkflowRig.TaskName, reading.Task);
                Assert.Equal(1, reading.Repeat);
            }

            Assert.Equal(WorkflowRig.OutputOf("Reviewer"), output.Text);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task Reviewer_starts_after_both_branches()
        {
            await using WorkflowRig rig = await WorkflowRig.CreateAsync((agent, time) => WorkflowRig.StreamingClient(time, agent, DelayOf(agent)));

            _ = await rig.RunAsync();

            StepReading researcher = ReadingOf(rig, "Researcher");
            StepReading writer = ReadingOf(rig, "Writer");
            StepReading reviewer = ReadingOf(rig, "Reviewer");
            Assert.True(
                reviewer.StartUtc >= (researcher.EndUtc > writer.EndUtc ? researcher.EndUtc : writer.EndUtc),
                $"The Reviewer window started at {reviewer.StartUtc:O}, before a branch ended (Researcher {researcher.EndUtc:O}, Writer {writer.EndUtc:O}).");

            // Independent of any timestamp: the one Reviewer call already carries the output of both branches.
            string prompt = UserTextOf(Assert.Single(rig.FakeOf("Reviewer").Calls));
            Assert.Contains(WorkflowRig.OutputOf("Researcher"), prompt, StringComparison.Ordinal);
            Assert.Contains(WorkflowRig.OutputOf("Writer"), prompt, StringComparison.Ordinal);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task Reviewer_starts_after_both_branches_in_serialized_mode()
        {
            await using WorkflowRig rig = await WorkflowRig.CreateAsync((agent, time) => WorkflowRig.StreamingClient(time, agent, DelayOf(agent)), serialized: true);

            _ = await rig.RunAsync();

            StepReading researcher = ReadingOf(rig, "Researcher");
            StepReading writer = ReadingOf(rig, "Writer");
            StepReading reviewer = ReadingOf(rig, "Reviewer");
            Assert.True(reviewer.StartUtc >= researcher.EndUtc, "The Reviewer started before the Researcher ended.");
            Assert.True(reviewer.StartUtc >= writer.EndUtc, "The Reviewer started before the Writer ended.");

            // The gate of serialized mode keeps the two branch windows apart.
            Assert.True(
                researcher.EndUtc <= writer.StartUtc || writer.EndUtc <= researcher.StartUtc,
                "The Researcher and Writer windows overlap in serialized mode.");
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task Researcher_and_Writer_run_at_the_same_time_in_concurrent_mode()
        {
            // Each branch waits, inside its call, for the other one to have started its call. A graph that ran the branches one after the other would leave the first
            // branch waiting for ever: the guard turns that into a failure after 30 seconds of real time.
            var researcherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using WorkflowRig rig = await WorkflowRig.CreateAsync((agent, time) => agent switch
            {
                "Researcher" => new RendezvousChatClient(WorkflowRig.StreamingClient(time, agent), researcherStarted, writerStarted.Task),
                "Writer" => new RendezvousChatClient(WorkflowRig.StreamingClient(time, agent), writerStarted, researcherStarted.Task),
                _ => WorkflowRig.StreamingClient(time, agent),
            });

            _ = await rig.RunAsync();

            StepReading researcher = ReadingOf(rig, "Researcher");
            StepReading writer = ReadingOf(rig, "Writer");
            Assert.True(researcher.StartUtc < writer.EndUtc && writer.StartUtc < researcher.EndUtc, "The branch windows do not overlap.");
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task Every_step_streams_and_records_a_measured_ttft()
        {
            await using WorkflowRig rig = await WorkflowRig.CreateAsync();

            _ = await rig.RunAsync();

            foreach (string agent in AgentNames)
            {
                Assert.True(Assert.Single(rig.FakeOf(agent).Calls).Streaming, $"{agent} made a buffered call.");
            }

            IReadOnlyList<StepReading> readings = rig.Readings();
            Assert.Equal(4, readings.Count);
            Assert.All(readings, reading =>
            {
                Assert.Equal(MetricLabel.Measured, reading.TtftMs.Label);
                Assert.NotNull(reading.TtftMs.Value);
                Assert.Equal("ms", reading.TtftMs.Unit);
            });
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task The_user_message_of_each_step_is_built_from_labelled_sections()
        {
            await using WorkflowRig rig = await WorkflowRig.CreateAsync();
            string prompt = WorkflowRig.TaskPrompt;
            string plan = WorkflowRig.OutputOf("Planner");

            _ = await rig.RunAsync();

            Assert.Equal("Task:\n" + prompt, UserTextOf(Assert.Single(rig.FakeOf("Planner").Calls)));
            Assert.Equal($"Task:\n{prompt}\n\nPlan:\n{plan}", UserTextOf(Assert.Single(rig.FakeOf("Researcher").Calls)));
            Assert.Equal($"Task:\n{prompt}\n\nPlan:\n{plan}", UserTextOf(Assert.Single(rig.FakeOf("Writer").Calls)));
            Assert.Equal(
                $"Task:\n{prompt}\n\nResearch:\n{WorkflowRig.OutputOf("Researcher")}\n\nDraft:\n{WorkflowRig.OutputOf("Writer")}",
                UserTextOf(Assert.Single(rig.FakeOf("Reviewer").Calls)));

            // The task text, the plan and the notes travel in user messages only: never in the instructions, never in a system message.
            foreach (string agent in AgentNames)
            {
                FakeChatCall call = Assert.Single(rig.FakeOf(agent).Calls);
                Assert.DoesNotContain(prompt, call.Options?.Instructions ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain(plan, call.Options?.Instructions ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain(call.Messages, message => message.Role == ChatRole.System);
                Assert.Null(call.Options?.Tools);
            }
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task A_failing_researcher_stops_the_workflow_before_the_reviewer()
        {
            const string providerText = "canary-provider-body-echo";
            var failure = new HttpRequestException(providerText, inner: null, HttpStatusCode.InternalServerError);
            await using WorkflowRig rig = await WorkflowRig.CreateAsync(FailingClient("Researcher", failure));

            StepFailedException thrown = await Assert.ThrowsAsync<StepFailedException>(() => rig.RunAsync());

            Assert.Equal("Researcher", thrown.Agent);
            Assert.Equal(nameof(HttpRequestException), thrown.FailureType);
            Assert.Equal(500, thrown.StatusCode);
            Assert.Equal(ExitCode.Unreachable, thrown.ExitCode);
            Assert.DoesNotContain(providerText, thrown.Message, StringComparison.Ordinal);
            Assert.Same(failure, thrown.InnerException);

            Assert.Equal(StepOutcomes.Error, ReadingOf(rig, "Researcher").Outcome);
            Assert.Empty(rig.ReadingsOf("Reviewer"));
            Assert.Empty(rig.FakeOf("Reviewer").Calls);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task A_failed_run_leaves_no_call_in_flight()
        {
            // In serialized mode a call that is still in flight holds the one gate slot. The run is over, with nothing in flight, when the failure is reported (the factory drains the
            // event stream, and StreamingRun.DisposeAsync also stops and waits for the run loop, so this test guards the outcome and not one of the two mechanisms).
            await using WorkflowRig rig = await WorkflowRig.CreateAsync(FailingClient("Researcher", new InvalidOperationException("canary-text")), serialized: true);

            _ = await Assert.ThrowsAsync<StepFailedException>(() => rig.RunAsync());

            Assert.Equal(1, Assert.IsType<SerializedMeterGate>(rig.Gate).CurrentCount);
            Assert.Equal(StepOutcomes.Error, ReadingOf(rig, "Researcher").Outcome);
            Assert.Equal(StepOutcomes.Ok, ReadingOf(rig, "Writer").Outcome);
            Assert.Empty(rig.ReadingsOf("Reviewer"));
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task A_reviewer_timeout_is_reported_with_the_reviewer_name()
        {
            // The engine swallows a cancellation thrown inside the delivery hook and raises no event; the SDK network timeout is such a cancellation. The run token is live.
            var failure = new TaskCanceledException("canary-timeout-text", new TimeoutException());
            await using WorkflowRig rig = await WorkflowRig.CreateAsync(FailingClient("Reviewer", failure));

            StepFailedException thrown = await Assert.ThrowsAsync<StepFailedException>(() => rig.RunAsync());

            Assert.Equal("Reviewer", thrown.Agent);
            Assert.Equal(nameof(TaskCanceledException), thrown.FailureType);
            Assert.Null(thrown.StatusCode);
            Assert.DoesNotContain("canary-timeout-text", thrown.Message, StringComparison.Ordinal);
            Assert.Equal(StepOutcomes.Error, ReadingOf(rig, "Reviewer").Outcome);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task A_reviewer_that_is_reached_by_one_branch_only_is_a_bug_and_not_a_step_failure()
        {
            await using WorkflowRig rig = await WorkflowRig.CreateAsync();
            var reviewer = new ReviewerExecutor(rig.Agents.Single(agent => agent.Name == "Reviewer"), WorkflowRig.TaskPrompt);
            IWorkflowContext context = DispatchProxy.Create<IWorkflowContext, ContextProxy>();
            await reviewer.HandleAsync(new BranchOutput(BranchRole.Writer, "draft"), context);

            InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await reviewer.ReviewAsync(context, CancellationToken.None));

            Assert.IsNotType<StepFailedException>(thrown);
            Assert.Null(reviewer.Failure);
            Assert.Empty(rig.FakeOf("Reviewer").Calls);
            Assert.Empty(rig.Readings());
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task A_failure_to_yield_the_output_is_a_graph_fault_and_not_a_reviewer_failure()
        {
            await using WorkflowRig rig = await WorkflowRig.CreateAsync();
            var reviewer = new ReviewerExecutor(rig.Agents.Single(agent => agent.Name == "Reviewer"), WorkflowRig.TaskPrompt);
            IWorkflowContext context = DispatchProxy.Create<IWorkflowContext, YieldFailsContextProxy>();
            await reviewer.HandleAsync(new BranchOutput(BranchRole.Researcher, "notes"), context);
            await reviewer.HandleAsync(new BranchOutput(BranchRole.Writer, "draft"), context);

            _ = await Assert.ThrowsAsync<NotSupportedException>(async () => await reviewer.ReviewAsync(context, CancellationToken.None));

            // The Reviewer's own call succeeded (one ok reading); only the yield failed, which no agent owns.
            Assert.Null(reviewer.Failure);
            Assert.Single(rig.FakeOf("Reviewer").Calls);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public void An_engine_fault_is_a_bug_that_names_only_the_exception_type()
        {
            var fault = new NotSupportedException("canary-engine-text");

            InvalidOperationException bug = AgentJoulesWorkflowFactory.EngineFault(fault);

            Assert.IsNotType<StepFailedException>(bug);
            Assert.IsNotType<AgentJoulesException>(bug, exactMatch: false);
            Assert.Contains(nameof(NotSupportedException), bug.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("canary-engine-text", bug.Message, StringComparison.Ordinal);
            Assert.Same(fault, bug.InnerException);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task The_error_line_of_a_step_failure_does_not_print_the_provider_exception()
        {
            // A key echoed by a provider error is built at run time; the inner exception keeps it (for the Debug log, behind the redactor) and the error line must not show it.
            string key = string.Concat("qv7k", "3mx9", "tz2p", "w8hd");
            await using WorkflowRig rig = await WorkflowRig.CreateAsync(FailingClient("Researcher", new HttpRequestException("rejected " + key, inner: null, HttpStatusCode.Unauthorized)));

            StepFailedException thrown = await Assert.ThrowsAsync<StepFailedException>(() => rig.RunAsync());

            Assert.Contains(key, Assert.IsType<HttpRequestException>(thrown.InnerException).Message, StringComparison.Ordinal);
            string line = ErrorLine.Format(thrown);
            Assert.Contains("Researcher", line, StringComparison.Ordinal);
            for (int at = 0; at + 6 <= key.Length; at++)
            {
                Assert.DoesNotContain(key.Substring(at, 6), line, StringComparison.Ordinal);
            }
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task Reasoning_text_is_not_passed_on_but_counts_for_the_time_to_the_first_token()
        {
            await using WorkflowRig rig = await WorkflowRig.CreateAsync((agent, time) => agent == "Planner"
                ? new FakeChatClient(time)
                {
                    Updates =
                    [
                        new FakeUpdate("canary-reasoning", TimeSpan.FromMilliseconds(300), Reasoning: true),
                        new FakeUpdate("the-plan", TimeSpan.FromMilliseconds(100)),
                    ],
                }
                : WorkflowRig.StreamingClient(time, agent));

            _ = await rig.RunAsync();

            string branchPrompt = UserTextOf(Assert.Single(rig.FakeOf("Researcher").Calls));
            Assert.EndsWith("Plan:\nthe-plan", branchPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("canary-reasoning", UserTextOf(Assert.Single(rig.FakeOf("Reviewer").Calls)), StringComparison.Ordinal);

            // The Planner runs alone, so its first update (the reasoning one) arrives after 300 ms of the 400 ms call.
            StepReading planner = ReadingOf(rig, "Planner");
            Assert.Equal(MetricLabel.Measured, planner.TtftMs.Label);
            Assert.InRange(planner.TtftMs.Value!.Value, 299, 301);
            Assert.True(planner.TtftMs.Value < planner.LatencyMs.Value);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task A_failing_reviewer_is_reported_with_its_own_name()
        {
            // The Reviewer runs in the hook that closes the delivery of the superstep, not in a message handler: its failure must still name the Reviewer.
            var failure = new HttpRequestException("canary-reviewer-body", inner: null, HttpStatusCode.BadGateway);
            await using WorkflowRig rig = await WorkflowRig.CreateAsync(FailingClient("Reviewer", failure));

            StepFailedException thrown = await Assert.ThrowsAsync<StepFailedException>(() => rig.RunAsync());

            Assert.Equal("Reviewer", thrown.Agent);
            Assert.Equal(nameof(HttpRequestException), thrown.FailureType);
            Assert.Equal(502, thrown.StatusCode);
            Assert.DoesNotContain("canary-reviewer-body", thrown.Message, StringComparison.Ordinal);
            Assert.Same(failure, thrown.InnerException);
            Assert.Equal(StepOutcomes.Error, ReadingOf(rig, "Reviewer").Outcome);
            Assert.Equal(StepOutcomes.Ok, ReadingOf(rig, "Researcher").Outcome);
            Assert.Equal(StepOutcomes.Ok, ReadingOf(rig, "Writer").Outcome);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task A_researcher_endpoint_that_answers_500_stops_the_workflow_before_the_reviewer()
        {
            // The real SDK client against a fake endpoint, composed by the real provider client factory: the status reaches the exception the workflow reports.
            await using WorkflowRig rig = await WorkflowRig.CreateWithFailingEndpointAsync("Researcher", status: 500);

            StepFailedException thrown = await Assert.ThrowsAsync<StepFailedException>(() => rig.RunAsync());

            Assert.Equal("Researcher", thrown.Agent);
            Assert.Equal("ClientResultException", thrown.FailureType);
            Assert.Equal(500, thrown.StatusCode);
            Assert.Equal(ExitCode.Unreachable, thrown.ExitCode);
            Assert.DoesNotContain(ProviderClientFixtures.FakeKey(), thrown.Message, StringComparison.Ordinal);

            // One request, no retry: only a 429 is retried.
            Assert.Equal(1, rig.Endpoint.Count(FakeRoute.ChatCompletions));
            Assert.Equal(StepOutcomes.Error, ReadingOf(rig, "Researcher").Outcome);
            Assert.Empty(rig.ReadingsOf("Reviewer"));
            Assert.Empty(rig.FakeOf("Reviewer").Calls);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task A_failing_planner_stops_both_branches()
        {
            await using WorkflowRig rig = await WorkflowRig.CreateAsync(FailingClient("Planner", new InvalidOperationException("canary-planner-text")));

            StepFailedException thrown = await Assert.ThrowsAsync<StepFailedException>(() => rig.RunAsync());

            Assert.Equal("Planner", thrown.Agent);
            Assert.Equal(nameof(InvalidOperationException), thrown.FailureType);
            Assert.Null(thrown.StatusCode);
            Assert.DoesNotContain("canary-planner-text", thrown.Message, StringComparison.Ordinal);

            StepReading reading = Assert.Single(rig.Readings());
            Assert.Equal("Planner", reading.Agent);
            Assert.Equal(StepOutcomes.Error, reading.Outcome);
            foreach (string agent in new[] { "Researcher", "Writer", "Reviewer" })
            {
                Assert.Empty(rig.FakeOf(agent).Calls);
            }
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task A_call_that_exhausted_its_429_retries_is_reported_with_its_status()
        {
            await using WorkflowRig rig = await WorkflowRig.CreateAsync(FailingClient("Writer", new RetriesExhaustedException(5, 429)));

            StepFailedException thrown = await Assert.ThrowsAsync<StepFailedException>(() => rig.RunAsync());

            Assert.Equal("Writer", thrown.Agent);
            Assert.Equal(nameof(RetriesExhaustedException), thrown.FailureType);
            Assert.Equal(429, thrown.StatusCode);
            Assert.Equal(StepOutcomes.Error, ReadingOf(rig, "Writer").Outcome);
            Assert.Empty(rig.ReadingsOf("Reviewer"));
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task A_cancelled_token_ends_the_run_with_a_cancellation_and_gives_the_gate_back()
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using WorkflowRig rig = await WorkflowRig.CreateAsync(
                (agent, time) => agent == "Planner" ? new BlockingChatClient(started, time) : WorkflowRig.StreamingClient(time, agent),
                serialized: true);
            using var cancellation = new CancellationTokenSource();

            Task<FinalOutput> run = rig.RunAsync(cancellation.Token);
            await started.Task.WaitAsync(HangGuard);
            await cancellation.CancelAsync();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(HangGuard));

            // A cancellation is not a step failure, and the stream of the Planner was disposed: the gate is free and the call has its reading.
            Assert.Equal(1, Assert.IsType<SerializedMeterGate>(rig.Gate).CurrentCount);
            Assert.Equal(StepOutcomes.Cancelled, Assert.Single(rig.Readings()).Outcome);
            Assert.Empty(rig.FakeOf("Researcher").Calls);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task A_token_that_is_already_cancelled_starts_no_call()
        {
            await using WorkflowRig rig = await WorkflowRig.CreateAsync();
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.RunAsync(cancellation.Token));

            Assert.Empty(rig.Readings());
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task A_second_run_of_one_factory_starts_from_a_clean_graph()
        {
            await using WorkflowRig rig = await WorkflowRig.CreateAsync();
            var factory = new AgentJoulesWorkflowFactory(rig.Agents, rig.Definition);

            FinalOutput first = await RunWithFactoryAsync(rig, factory);
            FinalOutput second = await RunWithFactoryAsync(rig, factory);

            Assert.Equal(first, second);
            Assert.Equal(8, rig.Readings().Count);

            // The executors hold per-run state (what the Reviewer collected), so the graph is built again for every run: the second Reviewer call is the first one's twin.
            IReadOnlyList<FakeChatCall> reviewerCalls = rig.FakeOf("Reviewer").Calls;
            Assert.Equal(2, reviewerCalls.Count);
            Assert.Equal(UserTextOf(reviewerCalls[0]), UserTextOf(reviewerCalls[1]));
            Assert.Equal(1, CountOf(UserTextOf(reviewerCalls[1]), "Research:"));
            Assert.Equal(1, CountOf(UserTextOf(reviewerCalls[1]), "Draft:"));
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public async Task The_factory_refuses_agents_that_are_not_the_four_of_the_catalog()
        {
            await using WorkflowRig rig = await WorkflowRig.CreateAsync();
            TaskDefinition task = rig.Definition;
            AIAgent[] agents = [.. rig.Agents];

            _ = Assert.Throws<ArgumentNullException>(() => new AgentJoulesWorkflowFactory(null!, task));
            _ = Assert.Throws<ArgumentNullException>(() => new AgentJoulesWorkflowFactory(agents, null!));
            _ = Assert.Throws<ArgumentException>(() => new AgentJoulesWorkflowFactory(agents[..3], task));
            _ = Assert.Throws<ArgumentException>(() => new AgentJoulesWorkflowFactory([agents[0], agents[0], agents[2], agents[3]], task));
            _ = Assert.Throws<ArgumentException>(() => new AgentJoulesWorkflowFactory([agents[0], agents[1], agents[2], null!], task));
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public void Executors_declare_the_message_types_they_send_and_yield()
        {
            Type planner = ExecutorType("PlannerExecutor");
            Type researcher = ExecutorType("ResearcherExecutor");
            Type writer = ExecutorType("WriterExecutor");
            Type reviewer = ExecutorType("ReviewerExecutor");

            Assert.Equal(typeof(Executor<TaskInput, PlanMessage>), planner.BaseType);
            Assert.Equal(typeof(Executor<PlanMessage, BranchOutput>), researcher.BaseType);
            Assert.Equal(typeof(Executor<PlanMessage, BranchOutput>), writer.BaseType);
            Assert.Equal(typeof(Executor<BranchOutput>), reviewer.BaseType);

            Assert.Equal([typeof(PlanMessage)], Declared<SendsMessageAttribute>(planner, attribute => attribute.Type));
            Assert.Equal([typeof(BranchOutput)], Declared<SendsMessageAttribute>(researcher, attribute => attribute.Type));
            Assert.Equal([typeof(BranchOutput)], Declared<SendsMessageAttribute>(writer, attribute => attribute.Type));
            Assert.Equal([typeof(FinalOutput)], Declared<YieldsOutputAttribute>(reviewer, attribute => attribute.Type));

            // Only the Reviewer yields output and only the Reviewer is the end of the graph: nothing else sends or yields a second type.
            Assert.Empty(Declared<YieldsOutputAttribute>(planner, attribute => attribute.Type));
            Assert.Empty(Declared<YieldsOutputAttribute>(researcher, attribute => attribute.Type));
            Assert.Empty(Declared<YieldsOutputAttribute>(writer, attribute => attribute.Type));
            Assert.Empty(Declared<SendsMessageAttribute>(reviewer, attribute => attribute.Type));

            Assert.All([planner, researcher, writer, reviewer], executor =>
            {
                Assert.True(executor.IsSealed);
                Assert.False(executor.IsPublic);
            });
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public void StepFailedException_maps_to_exit_5_and_carries_only_the_agent_the_type_and_the_status()
        {
            var inner = new InvalidOperationException("canary-inner-text");

            var failure = new StepFailedException("Researcher", "ClientResultException", 500, inner);

            Assert.Equal(ExitCode.Unreachable, failure.ExitCode);
            Assert.Equal(5, (int)failure.ExitCode);
            Assert.Equal("Researcher", failure.Agent);
            Assert.Equal("ClientResultException", failure.FailureType);
            Assert.Equal(500, failure.StatusCode);
            Assert.Same(inner, failure.InnerException);
            Assert.Equal("Step Researcher failed with ClientResultException and HTTP status 500", failure.Message);
            Assert.DoesNotContain("canary-inner-text", failure.Message, StringComparison.Ordinal);

            var withoutStatus = new StepFailedException("Planner", "TimeoutException");

            Assert.Null(withoutStatus.StatusCode);
            Assert.Null(withoutStatus.InnerException);
            Assert.Equal("Step Planner failed with TimeoutException", withoutStatus.Message);
            _ = Assert.IsType<AgentJoulesException>(withoutStatus, exactMatch: false);
        }

        [Fact]
        [Trait("Spec", "REQ-011")]
        public void StepFailedException_refuses_blank_names_and_a_status_that_is_not_http_and_echoes_clean_text()
        {
            _ = Assert.ThrowsAny<ArgumentException>(() => new StepFailedException(" ", "TimeoutException"));
            _ = Assert.ThrowsAny<ArgumentException>(() => new StepFailedException("Planner", string.Empty));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => new StepFailedException("Planner", "TimeoutException", 99));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => new StepFailedException("Planner", "TimeoutException", 600));

            var failure = new StepFailedException("Plan\nner" + new string('x', 100), "Type" + Bell + "Name");

            Assert.DoesNotContain('\n', failure.Message);
            Assert.DoesNotContain(Bell, failure.Message);
            Assert.True(failure.Message.Length < 200, "The message echoes the names without bound.");
            Assert.Contains(TextEcho.ForMessage("Plan\nner" + new string('x', 100)), failure.Message, StringComparison.Ordinal);
        }

        private static int CountOf(string text, string part)
        {
            int count = 0;
            for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        /// <summary>A context whose output channel refuses: yielding fails, as it does for an output type the executor did not declare.</summary>
        public class YieldFailsContextProxy : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
                targetMethod?.Name == "YieldOutputAsync" ? throw new NotSupportedException("The output type was not declared.") : targetMethod?.ReturnType == typeof(ValueTask) ? default(ValueTask) : null;
        }

        /// <summary>A context that does nothing: every member returns a completed task or null.</summary>
        public class ContextProxy : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
                targetMethod?.ReturnType == typeof(ValueTask) ? default(ValueTask) : null;
        }

        /// <summary>A streaming client that tells the other branch it has started, waits for the other branch to have started, and then streams what <paramref name="inner"/> streams.</summary>
        private sealed class RendezvousChatClient(FakeChatClient inner, TaskCompletionSource self, Task other) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("The workflow streams every call.");

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                _ = self.TrySetResult();
                await other.WaitAsync(HangGuard, cancellationToken);
                await foreach (ChatResponseUpdate update in inner.GetStreamingResponseAsync(messages, options, cancellationToken).WithCancellation(cancellationToken))
                {
                    yield return update;
                }
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

            public void Dispose()
            {
            }
        }

        /// <summary>A streaming client whose call starts, says so and then waits until the caller cancels it.</summary>
        private sealed class BlockingChatClient(TaskCompletionSource started, FakeTimeProvider time) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException("The workflow streams every call.");

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                _ = started.TrySetResult();

                // The call has run for a while on the fake clock before it is cancelled: a window that has no duration cannot be closed, and its reading would be lost.
                time.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield break;
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

            public void Dispose()
            {
            }
        }
    }
}