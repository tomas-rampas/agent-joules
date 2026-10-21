namespace AgentJoules.Core.Runs;

/// <summary>
/// The one rule of which tiers are measured (design 3.2, ADR-13): <c>local</c> and <c>cloudgpu</c> run on a GPU the meter reads over NVML; <c>nvidia</c> and <c>nebius</c> are hosted, their GPU
/// is not ours, and their energy is N/A with the reason <c>hosted-tier</c>. The preflight, the orchestrator, the run assembler and the step reading all ask here.
/// </summary>
public static class TierKinds
{
    /// <summary>True for a tier whose GPU is measured (<c>local</c>, <c>cloudgpu</c>).</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>True for <see cref="Tier.Local"/> and <see cref="Tier.CloudGpu"/>; false for every other value.</returns>
    public static bool IsMeasured(Tier tier) => tier is Tier.Local or Tier.CloudGpu;

    /// <summary>True for a hosted tier (<c>nvidia</c>, <c>nebius</c>), whose energy is N/A.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>True for <see cref="Tier.Nvidia"/> and <see cref="Tier.Nebius"/>; false for every other value.</returns>
    public static bool IsHosted(Tier tier) => tier is Tier.Nvidia or Tier.Nebius;
}