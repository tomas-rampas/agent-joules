using System.Globalization;

using AgentJoules.Core.Energy;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Runs;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-072: local electricity cost in EUR, cloudgpu rental cost in USD and the emission estimate. Scoped to the feature because sentences such as "the cost is computed" are reused by other stories
/// with other meanings. The state lives in <see cref="LocalAndCloudCostContext"/>; the real calculator and the real run assembler run over fixture readings, with no HTTP and no command line.
/// </summary>
[Binding]
[Scope(Feature = "AJ-072 Compute local electricity cost in EUR and cloudgpu rental cost in USD")]
public sealed class LocalAndCloudCostSteps(LocalAndCloudCostContext context)
{
    private readonly LocalAndCloudCostContext _context = context;

    private Metric Cost => _context.ComputedCost ?? throw new XunitException("No cost was computed.");

    private Metric Gco2e => (_context.ComputedTotals ?? throw new XunitException("No totals were assembled.")).Gco2e;

    private static string Number(double? value) => value is { } number ? number.ToString("R", CultureInfo.InvariantCulture) : "null";

    private static MetricLabel LabelNamed(string name) => name switch
    {
        "estimated" => MetricLabel.Estimated,
        "N/A" => MetricLabel.NotApplicable,
        "measured" => MetricLabel.Measured,
        "not-reported" => MetricLabel.NotReported,
        _ => throw new XunitException($"Unknown label word {name}."),
    };

    [Given(@"^net energy of (\d+) J and an electricity price of ([0-9.]+) (\w+) per kWh$")]
    public void GivenNetEnergyAndAnElectricityPrice(double joules, double price, string currency)
    {
        _context.Energy = CostFixtures.LocalEnergy(joules);
        _context.Cost.ElectricityPricePerKwh = price;
        _context.Cost.Currency = currency;
    }

    [Given(@"^an hourly price of ([0-9.]+) USD and a step wall time of (\d+) seconds$")]
    public void GivenAnHourlyPriceAndAWallTime(double price, double seconds)
    {
        _context.CloudGpu.HourlyPriceUsd = price;
        _context.WallSeconds = seconds;
        _context.Energy = CostFixtures.LocalEnergy(100.0);
    }

    [Given(@"^net energy of (\d+) J and a factor of ([0-9.]+) gCO2e per kWh$")]
    public void GivenNetEnergyAndAFactor(double joules, double factor)
    {
        _context.Energy = CostFixtures.LocalEnergy(joules);
        _context.Cost.GCo2ePerKwh = factor;
    }

    [Given(@"^a local step whose energy is N/A with reason ""([^""]*)""$")]
    public void GivenALocalStepWithoutEnergy(string reason) => _context.Energy = CostFixtures.LocalEnergyAbsent(reason);

    [Given(@"^a local step with negative net joules and flag ""([^""]*)""$")]
    public void GivenALocalStepBelowTheBaseline(string flag)
    {
        // The reading refuses a negative net without the flag, so the flag named by the scenario is the one the fixture sets.
        Assert.Equal(ReadingFlags.BelowBaseline, ReadingFlagNames.Parse(flag));
        _context.Energy = CostFixtures.LocalEnergy(-60.0);
    }

    [Given(@"^CloudGpu:HourlyPriceUsd is not configured$")]
    public void GivenNoHourlyPrice()
    {
        _context.CloudGpu.HourlyPriceUsd = null;
        _context.Energy = CostFixtures.LocalEnergy(100.0);
    }

    [Given(@"^Cost:GCo2ePerKwh is null$")]
    public void GivenNoEmissionFactor() => _context.Cost.GCo2ePerKwh = null;

    [When(@"^the local cost is computed$")]
    public void WhenTheLocalCostIsComputed() => _context.Compute(Tier.Local);

    [When(@"^the cloudgpu cost is computed$")]
    public void WhenTheCloudgpuCostIsComputed() => _context.Compute(Tier.CloudGpu);

    [When(@"^emissions are computed$")]
    public void WhenEmissionsAreComputed() => _context.AssembleTotals();

    [When(@"^the run totals are assembled$")]
    public void WhenTheRunTotalsAreAssembled() => _context.AssembleTotals();

    [Then(@"^the cost is ([0-9.]+) with label ""([^""]*)"" and currency ""([^""]*)""$")]
    public void ThenTheCostIsEstimated(double value, string label, string currency)
    {
        Metric cost = Cost;

        Assert.Equal(LabelNamed(label), cost.Label);
        Assert.True(cost.Value == value, $"Expected exactly {Number(value)}, got {Number(cost.Value)}.");
        Assert.Equal(currency, cost.Currency);
        Assert.Null(cost.Reason);
    }

    [Then(@"^the cost has label ""([^""]*)"", reason ""([^""]*)"" and a null value$")]
    public void ThenTheCostIsNotApplicableWithANullValue(string label, string reason)
    {
        Metric cost = Cost;

        Assert.Equal(LabelNamed(label), cost.Label);
        Assert.Equal(reason, cost.Reason);
        Assert.Null(cost.Value);
        Assert.Null(cost.Currency);
    }

    [Then(@"^the cost has label ""([^""]*)"" and reason ""([^""]*)""$")]
    public void ThenTheCostHasLabelAndReason(string label, string reason)
    {
        Metric cost = Cost;

        Assert.Equal(LabelNamed(label), cost.Label);
        Assert.Equal(reason, cost.Reason);
        Assert.Null(cost.Value);
    }

    // Structural check: EnergyReading is a record the calculator cannot change, so this step restates the
    // story's promise (the negative net stays in the reading) rather than catching a regression.
    [Then(@"^the net joules remain negative in the reading$")]
    public void ThenTheNetJoulesRemainNegative()
    {
        EnergyReading energy = _context.Energy ?? throw new XunitException("No energy window was set.");

        Assert.Equal(MetricLabel.Measured, energy.NetJoules.Label);
        Assert.True(energy.NetJoules.Value < 0.0, $"Expected a negative net, got {Number(energy.NetJoules.Value)}.");
        Assert.True(energy.Flags.HasFlag(ReadingFlags.BelowBaseline));
    }

    [Then(@"^totals gco2e is ([0-9.]+) with label ""([^""]*)""$")]
    public void ThenGco2eIsEstimated(double value, string label)
    {
        Metric gco2e = Gco2e;

        Assert.Equal(LabelNamed(label), gco2e.Label);
        Assert.True(gco2e.Value == value, $"Expected exactly {Number(value)}, got {Number(gco2e.Value)}.");
        Assert.Equal("gCO2e", gco2e.Unit);
        Assert.Null(gco2e.Reason);
    }

    [Then(@"^totals gco2e has label ""([^""]*)"" and reason ""([^""]*)""$")]
    public void ThenGco2eIsNotApplicable(string label, string reason)
    {
        Metric gco2e = Gco2e;

        Assert.Equal(LabelNamed(label), gco2e.Label);
        Assert.Equal(reason, gco2e.Reason);
        Assert.Null(gco2e.Value);
    }
}