using AgentJoules.Core.Runs;

namespace AgentJoules.Workflow.Local;

/// <summary>
/// Tells the orchestrator which <see cref="ILocalRuntime"/> warms up and describes a measured tier (REQ-031, K2 of M6, 2026-10-26): the configured local runtime for the local tier, a
/// <see cref="NimRuntime"/> bound to the cloudgpu tier for the cloudgpu tier, and nothing for a hosted tier.
/// </summary>
public interface ITierRuntimes
{
    /// <summary>Gets the runtime that serves <paramref name="tier"/>.</summary>
    /// <param name="tier">A defined tier.</param>
    /// <returns>The runtime, or null for a tier that has none (a hosted tier gets no warm-up and no description).</returns>
    ILocalRuntime? RuntimeOf(Tier tier);
}