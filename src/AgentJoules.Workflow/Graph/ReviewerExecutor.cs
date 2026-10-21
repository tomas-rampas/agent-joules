using AgentJoules.Workflow.Agents;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace AgentJoules.Workflow.Graph;

/// <summary>
/// The end of the graph, behind the fan-in barrier: collects the output of both branches and, once the messages of the superstep were delivered, streams the
/// Reviewer over the task, the research notes and the draft and yields the answer as the one <see cref="FinalOutput"/> of the workflow. The id of the executor is
/// the agent name.
/// </summary>
/// <remarks>
/// <para>
/// The executor holds per-run state (what the branches delivered), which is why the graph is built again for every run. The barrier edge delivers the output of
/// both branches in one superstep, so <see cref="OnMessageDeliveryFinishedAsync"/> (Microsoft.Agents.AI.Workflows 1.23.0: <c>protected internal virtual</c> on
/// <see cref="Executor"/>, called once per superstep after all messages were delivered) sees both. A superstep that delivered only one branch is a fault of the
/// graph and fails the step, never a silent review of half the input.
/// </para>
/// </remarks>
/// <param name="agent">The Reviewer agent; its pipeline is the metered one that the agent factory built.</param>
/// <param name="taskPrompt">The task prompt, which the Reviewer reads in the section <c>Task</c> of its user message.</param>
[YieldsOutput(typeof(FinalOutput))]
internal sealed partial class ReviewerExecutor(AIAgent agent, string taskPrompt) : Executor<BranchOutput>(AgentCatalog.Reviewer)
{
    private readonly AIAgent _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    private readonly string _taskPrompt = taskPrompt ?? throw new ArgumentNullException(nameof(taskPrompt));

    private string? _research;
    private string? _draft;

    /// <summary>
    /// The exception that ended the Reviewer step, or null. The engine reports a failure of the delivery hook as an error of the run and not as a failure of this executor, and it
    /// swallows a cancellation thrown there (the SDK network timeout is one) without raising any event, so the workflow factory reads the failure here to attribute it to the Reviewer.
    /// </summary>
    internal Exception? Failure { get; private set; }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The branch delivered twice.</exception>
    public override ValueTask HandleAsync(BranchOutput message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        switch (message.Role)
        {
            case BranchRole.Researcher when _research is null:
                _research = message.Text;
                break;
            case BranchRole.Writer when _draft is null:
                _draft = message.Text;
                break;
            default:
                throw new InvalidOperationException("A branch delivered its output to the Reviewer more than once, or the role of the output is not one of the two branches.");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Only one of the two branches delivered its output.</exception>
    protected override ValueTask OnMessageDeliveryFinishedAsync(IWorkflowContext context, CancellationToken cancellationToken = default) => ReviewAsync(context, cancellationToken);

    /// <summary>Reviews what the branches delivered; the body of the delivery hook, apart from it so that a test can call it.</summary>
    /// <exception cref="InvalidOperationException">Only one of the two branches delivered its output: a fault of the graph, not a failure of the Reviewer, so it is not recorded in <see cref="Failure"/>.</exception>
    internal async ValueTask ReviewAsync(IWorkflowContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_research is null && _draft is null)
        {
            return;
        }

        if (_research is null || _draft is null)
        {
            throw new InvalidOperationException("The Reviewer was reached before both branches delivered their output.");
        }

        string research = _research;
        string draft = _draft;
        _research = null;
        _draft = null;

        string answer;
        try
        {
            answer = await AgentStreaming.RunAsync(_agent, AgentStreaming.ReviewerMessage(_taskPrompt, research, draft), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            Failure = failure;
            throw;
        }

        // Outside the try on purpose: a failure to yield (an output type that was not declared, a fault of the graph) is not a failure of the Reviewer agent and is not recorded.
        await context.YieldOutputAsync(new FinalOutput(answer), cancellationToken).ConfigureAwait(false);
    }
}