using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Runs;

namespace AgentJoules.TestSupport;

/// <summary>An <see cref="ICostCalculator"/> that answers one metric whatever the step is, for tests that need a fixed cost whatever the price table holds.</summary>
/// <param name="cost">The metric every call answers.</param>
public sealed class FixedCostCalculator(Metric cost) : ICostCalculator
{
    /// <inheritdoc />
    public Metric Compute(StepIdentity identity, TokenCounts tokens, EnergyReading energy, double wallSeconds) => cost;
}