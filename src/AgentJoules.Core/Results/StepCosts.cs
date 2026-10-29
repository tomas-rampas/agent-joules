using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Results;

/// <summary>
/// The one cost-sum rule of the project: the results totals (<c>RunAssembler</c>) and the live cards (the dashboard snapshot) both call <see cref="ByCurrency"/>, so a card's cumulative cost and the
/// file's <c>totals.costByCurrency</c> cannot differ by construction.
/// </summary>
public static class StepCosts
{
    /// <summary>The reason of every currency of the map when at least one step carries no estimated cost: a partial sum would understate the total.</summary>
    public const string MissingStepCostsReason = "missing-step-costs";

    /// <summary>
    /// The costs per currency, in ordinal currency order, under the all-steps rule: the sums when every step carries an estimated cost; every currency seen N/A <see cref="MissingStepCostsReason"/>
    /// when at least one step lacks one; empty when no step carries a cost (and for no steps).
    /// </summary>
    /// <param name="steps">The steps to sum.</param>
    /// <returns>The map of currency to cost.</returns>
    public static IReadOnlyDictionary<string, Metric> ByCurrency(IEnumerable<StepReading> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var sums = new SortedDictionary<string, double>(StringComparer.Ordinal);
        bool missing = false;
        foreach (StepReading step in steps)
        {
            if (step.Cost is { Label: MetricLabel.Estimated, Currency: { } currency, Value: { } value })
            {
                sums[currency] = (sums.TryGetValue(currency, out double sum) ? sum : 0.0) + value;
            }
            else
            {
                missing = true;
            }
        }

        var costs = new Dictionary<string, Metric>(StringComparer.Ordinal);
        foreach ((string currency, double sum) in sums)
        {
            costs[currency] = missing ? Metric.NotApplicable(MissingStepCostsReason) : Metric.Estimated(sum, currency);
        }

        return costs;
    }
}