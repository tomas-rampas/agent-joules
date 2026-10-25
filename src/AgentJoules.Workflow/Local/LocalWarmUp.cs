using AgentJoules.Core.Configuration;

namespace AgentJoules.Workflow.Local;

/// <summary>
/// The unmetered warm-up of a local tier (REQ-021), shared by the measured run and the calibration so that both load the model the same way before they measure anything.
/// </summary>
internal static class LocalWarmUp
{
    /// <summary>
    /// Warms up every distinct model the agents of the tier call, in <see cref="TierOptions.ResolvedModelIds"/> order (each model once, <c>Tiers:local:ModelId</c> first when an agent uses it), through <paramref name="runtime"/>; the load time is the sum of the non-null times the runtime observed.
    /// </summary>
    /// <param name="runtime">The runtime of the local tier.</param>
    /// <param name="tierOptions">The options of the tier whose models are loaded.</param>
    /// <param name="cancellationToken">Cancels the warm-up.</param>
    /// <returns>The summed load time, or null when the runtime observed none.</returns>
    internal static async Task<TimeSpan?> RunAsync(ILocalRuntime runtime, TierOptions tierOptions, CancellationToken cancellationToken)
    {
        TimeSpan? total = null;
        foreach (string modelId in tierOptions.ResolvedModelIds())
        {
            if (await runtime.WarmUpAsync(modelId, cancellationToken).ConfigureAwait(false) is { } load)
            {
                total = (total ?? TimeSpan.Zero) + load;
            }
        }

        return total;
    }
}