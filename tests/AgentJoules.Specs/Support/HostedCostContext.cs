using AgentJoules.Core.Configuration;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-071 scenario (hosted token cost), handed to the step class by Reqnroll context injection. It holds the price file the Given steps write into a
/// <see cref="TempWorkspace"/> (the repository is never written), the tier configuration the calculator takes its missing-price reasons from (the shipped one: nvidia is a free
/// tier), and the costs the calculator answered. The calculator runs for real over a table loaded from that file; there is no HTTP and no command line.
/// </summary>
public sealed class HostedCostContext : IDisposable
{
    /// <summary>The model ID of the row the Given steps write, unless a step names another.</summary>
    public const string ModelId = PriceFileFixtures.ModelId;

    private TempWorkspace? _workspace;
    private string? _pricePath;
    private CostCalculator? _calculator;

    /// <summary>The tier configuration of the scenario: the shipped missing-price reasons.</summary>
    public Dictionary<string, TierOptions> Tiers { get; } = CostFixtures.ShippedTiers();

    /// <summary>The costs the When steps computed, by agent name.</summary>
    public Dictionary<string, Metric> Costs { get; } = new(StringComparer.Ordinal);

    /// <summary>The cost of the single computation of a scenario, or null before it ran.</summary>
    public Metric? Cost { get; private set; }

    /// <summary>Writes a price file whose rows are the given JSON objects.</summary>
    /// <param name="rows">Each element is one complete row object as JSON text.</param>
    public void WriteRows(params string[] rows)
    {
        _workspace ??= TempWorkspace.CreateEmpty();
        _pricePath = _workspace.WriteFile("prices.json", "[" + string.Join(",", rows) + "]");
    }

    /// <summary>Computes the cost of one step with the calculator built from the price file and keeps it as <see cref="Cost"/>.</summary>
    /// <param name="tier">The tier of the step.</param>
    /// <param name="modelId">The model ID of the step.</param>
    /// <param name="tokens">The token counts of the step.</param>
    /// <returns>The cost.</returns>
    public Metric Compute(Tier tier, string modelId, TokenCounts tokens)
    {
        Cost = Calculator().Compute(CostFixtures.Identity(tier, modelId), tokens, CostFixtures.HostedEnergy(), 1.0);
        return Cost;
    }

    /// <summary>Computes the cost of the step of one agent and records it under the agent name.</summary>
    /// <param name="agent">The agent name.</param>
    /// <param name="tier">The tier of the step.</param>
    /// <param name="modelId">The model ID of the step.</param>
    /// <param name="tokens">The token counts of the step.</param>
    public void ComputeFor(string agent, Tier tier, string modelId, TokenCounts tokens) =>
        Costs[agent] = Calculator().Compute(CostFixtures.Identity(tier, modelId, agent), tokens, CostFixtures.HostedEnergy(), 1.0);

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();

    private CostCalculator Calculator() =>
        _calculator ??= new CostCalculator(PriceTable.Load(_pricePath ?? throw new InvalidOperationException("No price file was written.")), Tiers);
}