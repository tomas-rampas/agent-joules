using AgentJoules.Core.Labels;
using AgentJoules.Specs.Support;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-074: derive joules and cost per quality point only when both inputs exist (REQ-010). Scoped to the feature because sentences such as "the derived metrics are computed" are reused by other
/// stories with other meanings. The state lives in <see cref="DerivedMetricsContext"/>; the real <c>DerivedMetrics</c> functions run over the scalars of one step, with no HTTP and no judge model.
/// </summary>
[Binding]
[Scope(Feature = "AJ-074 Derive joules and cost per quality point only when both inputs exist")]
public sealed class DerivedMetricsSteps(DerivedMetricsContext context)
{
    private readonly DerivedMetricsContext _context = context;

    private Metric Joules => _context.JoulesPerQualityPoint ?? throw new XunitException("The derived metrics were not computed.");

    private Metric CostPoint => _context.CostPerQualityPoint ?? throw new XunitException("The derived metrics were not computed.");

    private static MetricLabel LabelNamed(string name) => name switch
    {
        "model-judged" => MetricLabel.ModelJudged,
        "estimated" => MetricLabel.Estimated,
        "N/A" => MetricLabel.NotApplicable,
        _ => throw new XunitException($"Unknown label word {name}."),
    };

    [Given(@"^a step with net energy (\d+) J and quality (\d+)$")]
    public void GivenAStepWithNetEnergyAndQuality(double joules, double quality)
    {
        _context.NetJoules = Metric.Measured(joules, "J");
        _context.Quality = Metric.ModelJudged(quality);
    }

    [Given(@"^a step with cost ([0-9.]+) (\w+) and quality (\d+)$")]
    public void GivenAStepWithCostAndQuality(double cost, string currency, double quality)
    {
        _context.Cost = Metric.Estimated(cost, currency);
        _context.Quality = Metric.ModelJudged(quality);
    }

    [Given(@"^a hosted step whose energy is N/A and whose quality is (\d+)$")]
    public void GivenAHostedStep(double quality)
    {
        _context.NetJoules = Metric.NotApplicable("hosted-tier");
        _context.Quality = Metric.ModelJudged(quality);
    }

    [Given(@"^a local step with net energy (\d+) J and quality N/A$")]
    public void GivenAnUnscoredLocalStep(double joules)
    {
        _context.NetJoules = Metric.Measured(joules, "J");
        _context.Cost = Metric.Estimated(0.08, "EUR");
        _context.Quality = Metric.NotApplicable("not-scored");
    }

    [Given(@"^a local step with negative net energy flagged ""([^""]*)"" and quality (\d+)$")]
    public void GivenABelowBaselineStep(string flag, double quality)
    {
        _context.NetJoules = Metric.Measured(-5, "J");
        _context.Flags = ReadingFlagNames.Parse(flag);
        _context.Quality = Metric.ModelJudged(quality);
    }

    [When(@"^the derived metrics are computed$")]
    public void WhenTheDerivedMetricsAreComputed() => _context.Compute();

    [Then(@"^joulesPerQualityPoint is ([0-9.]+) with label ""([^""]*)""$")]
    public void ThenJoulesPerQualityPointIs(double value, string label)
    {
        Assert.Equal(value, Joules.Value);
        Assert.Equal(LabelNamed(label), Joules.Label);
        Assert.Null(Joules.Reason);
        Assert.Null(Joules.Unit);
        Assert.Null(Joules.Currency);
    }

    [Then(@"^costPerQualityPoint is ([0-9.]+) with label ""([^""]*)"" and currency ""([^""]*)""$")]
    public void ThenCostPerQualityPointIs(double value, string label, string currency)
    {
        Assert.Equal(value, CostPoint.Value);
        Assert.Equal(LabelNamed(label), CostPoint.Label);
        Assert.Equal(currency, CostPoint.Currency);
        Assert.Null(CostPoint.Reason);
        Assert.Null(CostPoint.Unit);
    }

    [Then(@"^joulesPerQualityPoint has label ""([^""]*)"", reason ""([^""]*)"" and a null value$")]
    public void ThenJoulesPerQualityPointHasLabelReasonAndNoValue(string label, string reason)
    {
        Assert.Null(Joules.Value);
        Assert.Equal(LabelNamed(label), Joules.Label);
        Assert.Equal(reason, Joules.Reason);
    }

    [Then(@"^both derived metrics have label ""([^""]*)"" and a null value$")]
    public void ThenBothDerivedMetricsAreAbsent(string label)
    {
        foreach (Metric metric in new[] { Joules, CostPoint })
        {
            Assert.Null(metric.Value);
            Assert.Equal(LabelNamed(label), metric.Label);
            Assert.False(string.IsNullOrWhiteSpace(metric.Reason), "The absent metric carries no reason.");
        }
    }

    [Then(@"^joulesPerQualityPoint has label ""([^""]*)"" and reason ""([^""]*)""$")]
    public void ThenJoulesPerQualityPointHasLabelAndReason(string label, string reason)
    {
        Assert.Null(Joules.Value);
        Assert.Equal(LabelNamed(label), Joules.Label);
        Assert.Equal(reason, Joules.Reason);
    }
}