using System.Reflection;
using System.Runtime.CompilerServices;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.Metering;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Graph;
using AgentJoules.Workflow.Runs;

using Microsoft.Extensions.AI;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>
/// AJ-123 fix round 1 (gatekeeper B-1, EDGE-016): the failure the workflow reports. The sibling that is turned away by the stopped gate can fail before the call that dropped has finished publishing, so its refusal
/// must give way to the failure of the call that failed.
/// </summary>
[Trait("Spec", "EDGE-016")]
public sealed class WorkflowFailureChoiceTests
{
    private static readonly TimeSpan Lag = TimeSpan.FromMilliseconds(200);

    private static (string Agent, Exception? Failure) Refusal(string agent) => (agent, new IncompleteRunException());

    private static (string Agent, Exception? Failure) Drop(string agent) => (agent, new IOException("cut"));

    [Fact]
    public void A_refusal_that_was_kept_gives_way_to_a_failure_that_is_not_one()
    {
        (string Agent, Exception? Failure)? chosen = AgentJoulesWorkflowFactory.PreferFailure(Refusal("Writer"), Drop("Researcher"));

        Assert.Equal("Researcher", chosen!.Value.Agent);
    }

    [Fact]
    public void A_failure_that_was_kept_is_not_replaced_by_a_refusal_or_by_a_later_failure()
    {
        Assert.Equal("Researcher", AgentJoulesWorkflowFactory.PreferFailure(Drop("Researcher"), Refusal("Writer"))!.Value.Agent);
        Assert.Equal("Researcher", AgentJoulesWorkflowFactory.PreferFailure(Drop("Researcher"), Drop("Writer"))!.Value.Agent);
        Assert.Equal("Writer", AgentJoulesWorkflowFactory.PreferFailure(Refusal("Writer"), Refusal("Researcher"))!.Value.Agent);
        Assert.Equal("Writer", AgentJoulesWorkflowFactory.PreferFailure(null, Drop("Writer"))!.Value.Agent);
    }

    [Fact]
    public void A_refusal_wrapped_by_the_engine_is_still_a_refusal()
    {
        var wrapped = new TargetInvocationException(new AggregateException(new IncompleteRunException()));

        (string Agent, Exception? Failure)? chosen = AgentJoulesWorkflowFactory.PreferFailure(("Writer", wrapped), Drop("Researcher"));

        Assert.Equal("Researcher", chosen!.Value.Agent);
    }

    [Fact]
    public void An_aggregate_that_holds_a_refusal_and_a_real_failure_counts_as_a_real_failure_and_wins()
    {
        var mixed = new AggregateException(new IncompleteRunException(), new IOException("cut"));

        (string Agent, Exception? Failure)? chosen = AgentJoulesWorkflowFactory.PreferFailure(Refusal("Writer"), ("Researcher", mixed));

        Assert.Equal("Researcher", chosen!.Value.Agent);
        Assert.Same(mixed, chosen.Value.Failure);
    }

    [Fact]
    public void A_target_invocation_exception_without_an_inner_exception_is_a_failure_as_itself()
    {
        var bare = new TargetInvocationException(null);

        (string Agent, Exception? Failure)? chosen = AgentJoulesWorkflowFactory.PreferFailure(("Writer", bare), Drop("Researcher"));

        Assert.Equal("Writer", chosen!.Value.Agent);
        Assert.Equal("Researcher", AgentJoulesWorkflowFactory.PreferFailure(Refusal("Writer"), ("Researcher", bare))!.Value.Agent);
    }

    private static StepReading Incomplete(string agent) => RunAssembler.WithOutcome(StepReadingFixtures.Metered(agent), StepOutcomes.Incomplete);

    // The status and the agent of the partial file (M-1): the incomplete reading of the (task, repeat) names the agent only when the reported failure is the sibling's refusal, which has no reading of its own.
    [Fact]
    public void A_refusal_reported_alone_takes_the_status_and_the_agent_from_the_incomplete_reading_of_the_repeat()
    {
        var refusal = new StepFailedException("Writer", nameof(IncompleteRunException));

        (RunStatus status, StepFailedException failure) = RunOrchestrator.SettleFailure([StepReadingFixtures.Metered("Planner"), Incomplete("Researcher")], ("summarise", 1), refusal);

        Assert.Equal(RunStatus.Incomplete, status);
        Assert.Equal("Researcher", failure.Agent);
        Assert.Equal(nameof(IncompleteRunException), failure.FailureType);
    }

    [Fact]
    public void A_refusal_with_no_incomplete_reading_of_the_repeat_stays_a_failed_run_that_names_the_refused_agent()
    {
        var refusal = new StepFailedException("Writer", nameof(IncompleteRunException));

        (RunStatus status, StepFailedException failure) = RunOrchestrator.SettleFailure([Incomplete("Researcher")], ("other-task", 1), refusal);

        Assert.Equal(RunStatus.Failed, status);
        Assert.Same(refusal, failure);
    }

    [Fact]
    public void A_failure_that_is_not_a_refusal_keeps_its_agent_type_and_status_code_whatever_another_agent_read()
    {
        var real = new StepFailedException("Writer", "ClientResultException", 400);

        (RunStatus status, StepFailedException failure) = RunOrchestrator.SettleFailure([Incomplete("Researcher")], ("summarise", 1), real);

        Assert.Equal(RunStatus.Failed, status);
        Assert.Same(real, failure);
        Assert.Equal(400, failure.StatusCode);
    }

    [Fact]
    public async Task The_workflow_names_the_call_that_dropped_when_the_refusal_of_its_sibling_is_reported_first()
    {
        // The Writer is refused at once; the Researcher drops 200 ms of real time later (the clocks of the rig cannot order two agents that run at the same time). The refusal event precedes the failure event.
        await using WorkflowRig rig = await WorkflowRig.CreateAsync((agent, time) => agent switch
        {
            "Writer" => new FakeChatClient(time) { Updates = [new FakeUpdate("never yielded")], Failure = new IncompleteRunException(), FailAfterUpdates = 0 },
            "Researcher" => new DelayedFailureClient(Lag, new IOException("cut")),
            _ => WorkflowRig.StreamingClient(time, agent),
        });

        StepFailedException thrown = await Assert.ThrowsAsync<StepFailedException>(() => rig.RunAsync());

        Assert.Equal("Researcher", thrown.Agent);
        Assert.Equal(nameof(IOException), thrown.FailureType);
    }

    private sealed class DelayedFailureClient(TimeSpan delay, Exception failure) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                throw failure;
            }

            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}