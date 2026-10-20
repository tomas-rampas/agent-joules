using AgentJoules.Workflow.Agents;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace AgentJoules.Workflow.Graph;

/// <summary>
/// The second branch of the fan-out: streams the Writer over the task and the plan and sends the draft to the Reviewer as a <see cref="BranchOutput"/> of the role
/// <see cref="BranchRole.Writer"/>. The id of the executor is the agent name.
/// </summary>
/// <param name="agent">The Writer agent; its pipeline is the metered one that the agent factory built.</param>
[SendsMessage(typeof(BranchOutput))]
internal sealed partial class WriterExecutor(AIAgent agent) : Executor<PlanMessage, BranchOutput>(AgentCatalog.Writer)
{
    private readonly AIAgent _agent = agent ?? throw new ArgumentNullException(nameof(agent));

    /// <inheritdoc />
    public override async ValueTask<BranchOutput> HandleAsync(PlanMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        string draft = await AgentStreaming.RunAsync(_agent, AgentStreaming.BranchMessage(message.Prompt, message.Plan), cancellationToken).ConfigureAwait(false);
        return new BranchOutput(BranchRole.Writer, draft);
    }
}