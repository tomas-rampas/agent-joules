using AgentJoules.Workflow.Agents;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace AgentJoules.Workflow.Graph;

/// <summary>
/// The first branch of the fan-out: streams the Researcher over the task and the plan and sends the research notes to the Reviewer as a <see cref="BranchOutput"/>
/// of the role <see cref="BranchRole.Researcher"/>. The id of the executor is the agent name.
/// </summary>
/// <param name="agent">The Researcher agent; its pipeline is the metered one that the agent factory built.</param>
[SendsMessage(typeof(BranchOutput))]
internal sealed partial class ResearcherExecutor(AIAgent agent) : Executor<PlanMessage, BranchOutput>(AgentCatalog.Researcher)
{
    private readonly AIAgent _agent = agent ?? throw new ArgumentNullException(nameof(agent));

    /// <inheritdoc />
    public override async ValueTask<BranchOutput> HandleAsync(PlanMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        string notes = await AgentStreaming.RunAsync(_agent, AgentStreaming.BranchMessage(message.Prompt, message.Plan), cancellationToken).ConfigureAwait(false);
        return new BranchOutput(BranchRole.Researcher, notes);
    }
}