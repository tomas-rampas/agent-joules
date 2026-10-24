using AgentJoules.Core.Configuration;
using AgentJoules.Core.Cost;
using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;
using AgentJoules.Core.Runs;
using AgentJoules.TestSupport;
using AgentJoules.Workflow.Runs;

namespace AgentJoules.Specs.Support;

/// <summary>
/// The state of one AJ-072 scenario (local electricity cost, cloudgpu rental cost and emissions), handed to the step class by Reqnroll context injection. The Given steps set the
/// options and the closed energy window of the step; the When steps run the real <see cref="CostCalculator"/> over an empty price table (the local and cloudgpu tiers have no price row)
/// or the real <see cref="RunAssembler"/> over one reading. There is no HTTP and no command line.
/// </summary>
public sealed class LocalAndCloudCostContext : IDisposable
{
    private TempWorkspace? _workspace;

    /// <summary>The <c>Cost</c> section of the scenario: the defaults until a Given step sets a value.</summary>
    public CostOptions Cost { get; } = new();

    /// <summary>The <c>CloudGpu</c> section of the scenario: no hourly price until a Given step sets one.</summary>
    public CloudGpuOptions CloudGpu { get; } = new();

    /// <summary>The closed energy window of the step; null until a Given step sets it.</summary>
    public EnergyReading? Energy { get; set; }

    /// <summary>The wall time of the step in seconds.</summary>
    public double WallSeconds { get; set; } = 1.0;

    /// <summary>The cost of the single computation of a scenario, or null before it ran.</summary>
    public Metric? ComputedCost { get; private set; }

    /// <summary>The totals of the single assembly of a scenario, or null before it ran.</summary>
    public Totals? ComputedTotals { get; private set; }

    /// <summary>Computes the cost of one step of a tier with the calculator built from the scenario options and keeps it as <see cref="ComputedCost"/>.</summary>
    /// <param name="tier">The tier of the step: local or cloudgpu.</param>
    /// <returns>The cost.</returns>
    public Metric Compute(Tier tier)
    {
        _workspace ??= TempWorkspace.CreateEmpty();
        string path = _workspace.WriteFile("prices.json", "[]");
        var calculator = new CostCalculator(PriceTable.Load(path), CostFixtures.ShippedTiers(), Cost, CloudGpu);

        ComputedCost = calculator.Compute(
            CostFixtures.Identity(tier, "scenario/model"),
            TokenCounts.From(10, 20),
            Energy ?? throw new InvalidOperationException("No energy window was set."),
            WallSeconds);
        return ComputedCost;
    }

    /// <summary>Assembles the totals of a serialized local run of one step with the net energy of the window (or the default reading when no window was set) and keeps them as <see cref="ComputedTotals"/>.</summary>
    /// <returns>The totals.</returns>
    public Totals AssembleTotals()
    {
        Metric? net = Energy?.NetJoules;
        StepReading[] steps = [StepReadingFixtures.Metered(netJoules: net)];

        ComputedTotals = RunAssembler.ComputeTotals(Tier.Local, RunMode.Serialized, steps, [], wallClockMs: 10.0, cost: Cost);
        return ComputedTotals;
    }

    /// <inheritdoc />
    public void Dispose() => _workspace?.Dispose();
}