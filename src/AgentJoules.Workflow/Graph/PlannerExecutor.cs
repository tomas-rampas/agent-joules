using AgentJoules.Workflow.Agents;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace AgentJoules.Workflow.Graph;

/// <summary>
/// The start of the graph: streams the Planner over the task prompt and sends the plan to both branches. The returned value is sent automatically
/// (<see cref="ExecutorOptions.AutoSendMessageHandlerResultObject"/> is on by default), so <see cref="PlanMessage"/> is the one message type it sends. The id of the
/// executor is the agent name, which is how a failure of a step is attributed.
/// </summary>
/// <param name="agent">The Planner agent; its pipeline is the metered one that the agent factory built.</param>
[SendsMessage(typeof(PlanMessage))]
internal sealed partial class PlannerExecutor(AIAgent agent) : Executor<TaskInput, PlanMessage>(AgentCatalog.Planner)
{
    private readonly AIAgent _agent = agent ?? throw new ArgumentNullException(nameof(agent));

    /// <inheritdoc />
    public override async ValueTask<PlanMessage> HandleAsync(TaskInput message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        string plan = await AgentStreaming.RunAsync(_agent, AgentStreaming.PlannerMessage(message.Prompt), cancellationToken).ConfigureAwait(false);
        return new PlanMessage(message.Prompt, plan);
    }
}