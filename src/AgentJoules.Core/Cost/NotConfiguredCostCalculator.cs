using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Runs;

namespace AgentJoules.Core.Cost;

/// <summary>
/// The cost calculator of M2: no price table is configured, so the cost of every step is N/A with the reason <c>cost-not-configured</c>,
/// whatever the tokens, the energy or the time (REQ-008, EDGE-007). It never answers 0, because a cost that is not known is not zero. The
/// price-table calculator of M3 (<see cref="CostCalculator"/>) replaces it.
/// </summary>
/// <remarks>Stateless and thread-safe; <see cref="Instance"/> serves wherever one shared object is wanted.</remarks>
public sealed class NotConfiguredCostCalculator : ICostCalculator
{
    /// <summary>The reason every cost carries.</summary>
    public const string Reason = "cost-not-configured";

    /// <summary>The one shared calculator.</summary>
    public static NotConfiguredCostCalculator Instance { get; } = new();

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="identity"/>, <paramref name="tokens"/> or <paramref name="energy"/> is null.</exception>
    public Metric Compute(StepIdentity identity, TokenCounts tokens, EnergyReading energy, double wallSeconds)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(energy);

        return Metric.NotApplicable(Reason);
    }
}