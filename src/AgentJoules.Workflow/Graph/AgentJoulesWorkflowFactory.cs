using System.ClientModel;
using System.Reflection;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Tasks;
using AgentJoules.Metering;
using AgentJoules.Workflow.Agents;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

using WorkflowGraph = Microsoft.Agents.AI.Workflows.Workflow;

namespace AgentJoules.Workflow.Graph;

/// <summary>
/// Builds and runs the workflow of one (tier, task, repeat) (REQ-011, ADR-10): Planner, then Researcher and Writer in parallel, then a fan-in barrier, then Reviewer.
/// It takes the four agents that <see cref="AgentFactory.CreateAll"/> made and the task, and <see cref="RunAsync"/> returns the <see cref="FinalOutput"/> of the Reviewer.
/// </summary>
/// <remarks>
/// <para>
/// Readings reach the sink through the metered pipelines of the agents, so the factory needs no access to the sink: the reading of a step that failed is already
/// published when the failure arrives here. The run executes through <see cref="InProcessExecution"/> and its event stream; the executors of one superstep run through
/// <c>Task.WhenAll</c>, so in concurrent mode the two branches really overlap and in serialized mode the meter gate keeps their windows apart.
/// </para>
/// <para>
/// Every <see cref="RunAsync"/> builds the graph again, because the executors hold per-run state (what the Reviewer collected), so one factory may run more than once;
/// the agents and their pipelines are the ones given and are shared by the runs. A factory belongs to one tier run: the agent factory builds new agents, with new
/// pipelines and ledgers, for every repeat.
/// </para>
/// <para>
/// A step that fails ends the run with a <see cref="StepFailedException"/> (exit code 5) that names the agent and carries the type of the failure and its HTTP status, never
/// its message: the message of a provider exception holds the response body, which can echo the credential. The run is watched to its end first (and disposing the run also waits for its loop), so no call of the
/// other branch is still in flight when the exception is raised, and its meter gate and window are closed. When both branches fail, the failure that was reported first is the one raised. A cancellation of the run token is not a step failure and
/// propagates as <see cref="OperationCanceledException"/>. An error of the run that no agent owns is a bug, raised as <see cref="InvalidOperationException"/> (exit code 1). Task text, plan and notes appear only in the user messages of the steps.
/// </para>
/// </remarks>
public sealed class AgentJoulesWorkflowFactory
{
    private readonly AIAgent _planner;
    private readonly AIAgent _researcher;
    private readonly AIAgent _writer;
    private readonly AIAgent _reviewer;
    private readonly TaskDefinition _task;

    /// <summary>A test seam: true puts the messages between the executors into the workflow spans. Internal, so no configuration or command line reaches it; it is false in every run.</summary>
    internal bool EnableSensitiveData { get; init; }

    /// <summary>Creates the factory for the four agents of a repeat and the task they run.</summary>
    /// <param name="agents">Exactly the four agents of the catalog, in any order, each by its catalog name; from <see cref="AgentFactory.CreateAll"/>.</param>
    /// <param name="task">The task whose prompt is the input of the workflow.</param>
    /// <exception cref="ArgumentNullException"><paramref name="agents"/> or <paramref name="task"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="agents"/> does not hold exactly one agent for each of <see cref="AgentCatalog.Names"/>, or holds a null.</exception>
    public AgentJoulesWorkflowFactory(IReadOnlyList<AIAgent> agents, TaskDefinition task)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(task);

        if (agents.Count != AgentCatalog.Names.Count || agents.Any(static agent => agent is null))
        {
            throw new ArgumentException("The workflow needs exactly four agents: Planner, Researcher, Writer and Reviewer.", nameof(agents));
        }

        _planner = Single(agents, AgentCatalog.Planner);
        _researcher = Single(agents, AgentCatalog.Researcher);
        _writer = Single(agents, AgentCatalog.Writer);
        _reviewer = Single(agents, AgentCatalog.Reviewer);
        _task = task;
    }

    /// <summary>Builds the graph and runs it once, watching its events until the run ends.</summary>
    /// <param name="cancellationToken">The run token; a cancellation ends the run with <see cref="OperationCanceledException"/>.</param>
    /// <returns>The output of the Reviewer.</returns>
    /// <exception cref="StepFailedException">A step failed (exit code 5); <see cref="StepFailedException.Agent"/> names the agent.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">The run ended without a final output and without a failed step.</exception>
    public async Task<FinalOutput> RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        (WorkflowGraph workflow, ReviewerExecutor reviewer) = Build();
        FinalOutput? output = null;
        (string Agent, Exception? Failure)? failed = null;
        Exception? engineFault = null;

        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, new TaskInput(_task.Prompt), cancellationToken: cancellationToken).ConfigureAwait(false);
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (evt)
            {
                case WorkflowOutputEvent { Data: FinalOutput final }:
                    output = final;
                    break;
                case ExecutorFailedEvent executorFailed:
                    failed = PreferFailure(failed, (executorFailed.ExecutorId, executorFailed.Data));
                    break;
                case WorkflowErrorEvent workflowError:
                    // The Reviewer runs in the delivery hook of its superstep, so its failure arrives here, or not at all (see below); an error that no agent owns is a fault of the engine.
                    engineFault ??= workflowError.Exception;
                    break;
                default:
                    break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The engine swallows a cancellation thrown inside the delivery hook (a Reviewer timeout) and raises no event for it, so the Reviewer recorded its own failure.
        failed ??= reviewer.Failure is { } late ? (AgentCatalog.Reviewer, late) : null;

        return failed is { } step
            ? throw Describe(step.Agent, step.Failure)
            : engineFault is not null
                ? throw EngineFault(engineFault)
                : output ?? throw new InvalidOperationException("The workflow ended without a final output.");
    }

    // The first failure is kept, except that a refusal of the stopped gate (the sibling that was queued behind a dropped call, AJ-123) gives way to a failure that is not one: the refusal can be reported
    // before the call that dropped has finished publishing its reading, and the run must be told by the call that failed, not by the call that was turned away (gatekeeper B-1).
    internal static (string Agent, Exception? Failure)? PreferFailure((string Agent, Exception? Failure)? kept, (string Agent, Exception? Failure) next) =>
        kept is null || (IsRefusal(kept.Value.Failure) && !IsRefusal(next.Failure)) ? next : kept;

    private static bool IsRefusal(Exception? failure) => failure is not null && Unwrap(failure) is IncompleteRunException;

    // An error of the run that no agent owns is a bug of the engine or of the graph, not a failed step: it is not exit code 5. Only the type of the fault is named.
    internal static InvalidOperationException EngineFault(Exception fault) =>
        new(string.Concat("The workflow engine reported an error of type ", Unwrap(fault).GetType().Name, "."), fault);

    private static AIAgent Single(IReadOnlyList<AIAgent> agents, string name)
    {
        AIAgent[] named = [.. agents.Where(agent => string.Equals(agent.Name, name, StringComparison.Ordinal))];
        return named.Length == 1
            ? named[0]
            : throw new ArgumentException($"The workflow needs exactly one agent named {name}.", nameof(agents));
    }

    // The failure of the step as it was thrown: the workflow may wrap an executor's exception, and only the exception itself says what happened.
    private static Exception Unwrap(Exception failure)
    {
        Exception current = failure;
        while (true)
        {
            if (current is TargetInvocationException { InnerException: { } wrapped })
            {
                current = wrapped;
            }
            else if (current is AggregateException { InnerExceptions.Count: 1 } aggregate)
            {
                current = aggregate.InnerExceptions[0];
            }
            else
            {
                return current;
            }
        }
    }

    // Only the type and the status of the failure leave this method: its message can hold the body of the provider's answer. The one exception is the in-stream error of a provider, which
    // keeps the line "provider-error: <type> <code> <message>": its text is masked, cleaned and bounded when the exception is made.
    private static StepFailedException Describe(string agent, Exception? failure)
    {
        if (failure is null)
        {
            return new StepFailedException(agent, "UnknownFailure");
        }

        Exception root = Unwrap(failure);
        int? status = root switch
        {
            ClientResultException { Status: >= 100 and <= 599 } client => client.Status,
            HttpRequestException { StatusCode: { } httpStatus } when (int)httpStatus is >= 100 and <= 599 => (int)httpStatus,
            RetriesExhaustedException { LastProviderError: null } retries => retries.StatusCode,
            _ => null,
        };

        // The in-stream error is told in its own words: the exhaustion wording (the code is no HTTP status) and the text of the last error, both masked and bounded by construction.
        string? detail = root switch
        {
            ProviderErrorException error => error.StepText,
            RetriesExhaustedException { LastProviderError: { } last } retries => retries.Message + "; " + last.StepText,
            _ => null,
        };

        return new StepFailedException(agent, root.GetType().Name, status, root, detail);
    }

    private (WorkflowGraph Graph, ReviewerExecutor Reviewer) Build()
    {
        var planner = new PlannerExecutor(_planner);
        var researcher = new ResearcherExecutor(_researcher);
        var writer = new WriterExecutor(_writer);
        var reviewer = new ReviewerExecutor(_reviewer, _task.Prompt);

        // Workflow telemetry is off unless the builder asks for it (REQ-014). The spans of the sources Microsoft.Agents.AI.Workflows* carry metadata only: sensitive data is set explicitly
        // from the internal switch, false in every run, because it is what would put the task text, the plan and the notes (the messages between the executors) into a span.
        WorkflowGraph graph = new WorkflowBuilder(planner)
            .AddFanOutEdge(planner, [researcher, writer])
            .AddFanInBarrierEdge([researcher, writer], reviewer)
            .WithOutputFrom(reviewer)
            .WithOpenTelemetry(telemetry => telemetry.EnableSensitiveData = EnableSensitiveData)
            .Build();

        return (graph, reviewer);
    }
}