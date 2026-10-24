using System.Globalization;

using AgentJoules.Core.Cost;
using AgentJoules.Core.Labels;
using AgentJoules.Core.Runs;
using AgentJoules.Specs.Support;
using AgentJoules.TestSupport;

using Reqnroll;

using Xunit;
using Xunit.Sdk;

namespace AgentJoules.Specs.Steps;

/// <summary>
/// AJ-071: hosted token cost in USD, or N/A with a reason. Scoped to the feature because sentences such as "the cost is computed" are reused by other stories with other meanings.
/// The state lives in <see cref="HostedCostContext"/>. The calculator runs for real over a price table loaded from a file in a temporary folder; no HTTP and no command line is involved.
/// </summary>
[Binding]
[Scope(Feature = "AJ-071 Compute hosted token cost in USD or label it N/A")]
public sealed class HostedCostSteps(HostedCostContext context)
{
    private const long ReportedInput = 1_000_000;
    private const long ReportedOutput = 500_000;

    private readonly HostedCostContext _context = context;

    private Metric Cost => _context.Cost ?? throw new XunitException("No cost was computed.");

    private static string Number(double? value) => value is { } number ? number.ToString("R", CultureInfo.InvariantCulture) : "null";

    private static MetricLabel LabelNamed(string name) => name switch
    {
        "estimated" => MetricLabel.Estimated,
        "N/A" => MetricLabel.NotApplicable,
        "measured" => MetricLabel.Measured,
        "not-reported" => MetricLabel.NotReported,
        _ => throw new XunitException($"Unknown label word {name}."),
    };

    [Given(@"^a price row with ([0-9.]+) USD per 1M input and ([0-9.]+) USD per 1M output tokens$")]
    public void GivenAPriceRow(string input, string output) =>
        _context.WriteRows(PriceFileFixtures.Row(inputUsdPer1M: input, outputUsdPer1M: output));

    [Given(@"^rows for models ""([^""]*)"" and ""([^""]*)"" with different prices$")]
    public void GivenRowsForTwoModels(string first, string second) =>
        _context.WriteRows(
            PriceFileFixtures.Row(modelId: PriceFileFixtures.Quote(first), inputUsdPer1M: "0.10", outputUsdPer1M: "0.40"),
            PriceFileFixtures.Row(modelId: PriceFileFixtures.Quote(second), inputUsdPer1M: "1.00", outputUsdPer1M: "2.00"));

    [Given(@"^no price row for tier ""([^""]*)""$")]
    public void GivenNoPriceRowForTier(string tier)
    {
        // The only row belongs to another tier, so the tier under test has none.
        string otherTier = tier == "nebius" ? "\"nvidia\"" : PriceFileFixtures.TierNebius;
        _context.WriteRows(PriceFileFixtures.Row(tier: otherTier));
    }

    [Given(@"^no price row for the nebius model$")]
    public void GivenNoPriceRowForTheNebiusModel() =>
        _context.WriteRows(PriceFileFixtures.Row(modelId: PriceFileFixtures.Quote("vendor/another-model")));

    [Given(@"^a price row exists and the output tokens are not-reported$")]
    public void GivenARowAndUnreportedOutputTokens() => _context.WriteRows(PriceFileFixtures.Row());

    [When(@"^the cost of (\d+) input and (\d+) output tokens is computed$")]
    public void WhenTheCostOfTokensIsComputed(long input, long output) =>
        _context.Compute(Tier.Nebius, HostedCostContext.ModelId, TokenCounts.From(input, output));

    [When(@"^the Writer uses ""([^""]*)"" and the Planner uses ""([^""]*)""$")]
    public void WhenTheAgentsUseTheirOwnModels(string writerModel, string plannerModel)
    {
        TokenCounts tokens = TokenCounts.From(ReportedInput, ReportedInput);
        _context.ComputeFor("Writer", Tier.Nebius, writerModel, tokens);
        _context.ComputeFor("Planner", Tier.Nebius, plannerModel, tokens);
    }

    [When(@"^the cost of a nvidia step is computed$")]
    public void WhenTheCostOfANvidiaStepIsComputed() =>
        _context.Compute(Tier.Nvidia, HostedCostContext.ModelId, TokenCounts.From(ReportedInput, ReportedOutput));

    [When(@"^the cost of a nebius step is computed$")]
    public void WhenTheCostOfANebiusStepIsComputed() =>
        _context.Compute(Tier.Nebius, HostedCostContext.ModelId, TokenCounts.From(ReportedInput, ReportedOutput));

    [When(@"^the cost is computed$")]
    public void WhenTheCostIsComputed() =>
        _context.Compute(Tier.Nebius, HostedCostContext.ModelId, TokenCounts.From(ReportedInput, null));

    [Then(@"^the cost is ([0-9.]+) with label ""([^""]*)"" and currency ""([^""]*)""$")]
    public void ThenTheCostIsEstimated(double value, string label, string currency)
    {
        Metric cost = Cost;

        Assert.Equal(LabelNamed(label), cost.Label);
        Assert.True(cost.Value == value, $"Expected exactly {Number(value)}, got {Number(cost.Value)}.");
        Assert.Equal(currency, cost.Currency);
        Assert.Null(cost.Reason);
    }

    [Then(@"^each cost is computed from its own model row$")]
    public void ThenEachCostIsComputedFromItsOwnRow()
    {
        // One million input and one million output tokens: model-a is 0.10 + 0.40, model-b is 1.00 + 2.00.
        Assert.Equal(3.00, _context.Costs["Writer"].Value);
        Assert.Equal(0.50, _context.Costs["Planner"].Value);
        Assert.All(_context.Costs.Values, cost =>
        {
            Assert.Equal(MetricLabel.Estimated, cost.Label);
            Assert.Equal("USD", cost.Currency);
        });
    }

    [Then(@"^the cost has label ""([^""]*)"", reason ""([^""]*)"" and a null value$")]
    public void ThenTheCostIsNotApplicable(string label, string reason)
    {
        Metric cost = Cost;

        Assert.Equal(LabelNamed(label), cost.Label);
        Assert.Equal(reason, cost.Reason);
        Assert.Null(cost.Value);
    }
}