using AgentJoules.Core.Labels;
using AgentJoules.Core.Results;

using Xunit;

namespace AgentJoules.UnitTests;

/// <summary>The one cost-sum rule shared by the results totals and the live cards: the all-steps rule of <see cref="StepCosts.ByCurrency"/>.</summary>
public sealed class StepCostsTests
{
    [Fact]
    public void Priced_steps_sum_per_currency_as_estimated()
    {
        IReadOnlyDictionary<string, Metric> costs = StepCosts.ByCurrency([Priced(0.25, "USD"), Priced(0.5, "USD")]);

        KeyValuePair<string, Metric> only = Assert.Single(costs);
        Assert.Equal("USD", only.Key);
        Assert.Equal(MetricLabel.Estimated, only.Value.Label);
        Assert.Equal(0.75, only.Value.Value);
        Assert.Equal("USD", only.Value.Currency);
    }

    [Fact]
    public void Two_currencies_sit_side_by_side_in_ordinal_order()
    {
        IReadOnlyDictionary<string, Metric> costs = StepCosts.ByCurrency([Priced(1.0, "USD"), Priced(2.0, "EUR"), Priced(3.0, "USD")]);

        Assert.Equal(["EUR", "USD"], costs.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2.0, costs["EUR"].Value);
        Assert.Equal(4.0, costs["USD"].Value);
    }

    [Fact]
    public void One_unpriced_step_makes_every_entry_not_applicable_missing_step_costs()
    {
        IReadOnlyDictionary<string, Metric> costs = StepCosts.ByCurrency([Priced(1.0, "USD"), Priced(2.0, "EUR"), Unpriced()]);

        Assert.Equal(2, costs.Count);
        foreach (Metric metric in costs.Values)
        {
            Assert.Equal(MetricLabel.NotApplicable, metric.Label);
            Assert.Null(metric.Value);
            Assert.Equal(StepCosts.MissingStepCostsReason, metric.Reason);
        }
    }

    // The Estimated-label check of ByCurrency is unreachable through a StepReading: its constructor (RequireCostShape) rejects every valued cost
    // that is not estimated, so no test can hand the helper a measured or model-judged cost. The check stays as a guard for a future reading shape.

    [Fact]
    public void No_steps_gives_an_empty_map()
    {
        Assert.Empty(StepCosts.ByCurrency([]));
    }

    [Fact]
    public void Only_unpriced_steps_give_an_empty_map()
    {
        Assert.Empty(StepCosts.ByCurrency([Unpriced()]));
    }

    [Fact]
    public void The_reason_is_missing_step_costs()
    {
        Assert.Equal("missing-step-costs", StepCosts.MissingStepCostsReason);
    }

    private static StepReading Priced(double value, string currency) => DashboardEventSerializationTests.Reading(cost: Metric.Estimated(value, currency));

    private static StepReading Unpriced() => DashboardEventSerializationTests.Reading();
}