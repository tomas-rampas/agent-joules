using AgentJoules.Core.Configuration;

using Microsoft.Extensions.AI;

namespace AgentJoules.Workflow.Agents;

/// <summary>
/// Builds the <see cref="ChatOptions"/> of one agent call (REQ-012, design 3.4): the same for every tier so that a difference between two tiers is
/// the model and the hardware, not the sampling. Temperature is always 0, the output cap is the task's, the model is the per-agent one, and the seed
/// is sent only to a tier that honours it. The name of the JSON property that carries the cap is not chosen here: the request body patch of the tier
/// renames it (<c>Tiers:&lt;tier&gt;:MaxTokensField</c>).
/// </summary>
public static class ChatOptionsFactory
{
    /// <summary>Creates the options of a call of <paramref name="agent"/>.</summary>
    /// <param name="tier">The options of the tier the call goes to.</param>
    /// <param name="agent">The agent name, for example <c>Planner</c>; picks <c>AgentModelIds</c> over <c>ModelId</c>.</param>
    /// <param name="maxOutputTokens">The <c>maxOutputTokens</c> of the task; greater than 0.</param>
    /// <param name="seed">The request seed (<c>Workflow:Seed</c>); used only when <see cref="TierOptions.SupportsSeed"/> is true.</param>
    /// <returns>Options with <see cref="ChatOptions.Temperature"/> 0, <see cref="ChatOptions.MaxOutputTokens"/> set, <see cref="ChatOptions.ModelId"/> resolved and <see cref="ChatOptions.Seed"/> set or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tier"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="agent"/> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxOutputTokens"/> is not greater than 0.</exception>
    /// <exception cref="Core.Cli.ConfigurationException">The agent has no model ID (see <see cref="TierOptions.ResolveModelId(string)"/>).</exception>
    public static ChatOptions Create(TierOptions tier, string agent, int maxOutputTokens, int seed)
    {
        ArgumentNullException.ThrowIfNull(tier);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputTokens);

        return new ChatOptions
        {
            Temperature = 0,
            MaxOutputTokens = maxOutputTokens,
            ModelId = tier.ResolveModelId(agent),
            Seed = tier.SupportsSeed ? seed : null,
        };
    }
}