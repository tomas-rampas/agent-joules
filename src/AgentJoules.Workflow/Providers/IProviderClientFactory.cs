using AgentJoules.Core.Runs;

using Microsoft.Extensions.AI;

namespace AgentJoules.Workflow.Providers;

/// <summary>
/// Creates the provider chat client of a tier for one model (REQ-011, REQ-012): the bottom of the pipeline of an agent. The agent factory depends on this
/// interface and not on <see cref="ProviderClientFactory"/>, so a test hands it a fake client per agent and no request leaves the process.
/// </summary>
public interface IProviderClientFactory
{
    /// <summary>Creates the chat client of <paramref name="tier"/> for <paramref name="modelId"/>; every call returns a new client.</summary>
    /// <param name="tier">A defined tier.</param>
    /// <param name="modelId">The model ID of the call, from <see cref="Core.Configuration.TierOptions.ResolveModelId(string)"/>.</param>
    /// <returns>A chat client; the pipeline of the agent that owns it decorates it, and nothing below the agent shares it.</returns>
    IChatClient Create(Tier tier, string modelId);
}