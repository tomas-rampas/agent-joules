using System.Text.Json.Serialization;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Results;

/// <summary>
/// The <c>totals</c> object of the results file (REQ-015): the sums over the steps of the workflow, judge calls excluded (they are in
/// <see cref="Judge"/>). Every number is a label object, so a sum that cannot be formed is N/A or not-reported, never 0. The names are the camelCase
/// names of the prompt skeleton; <see cref="Gco2e"/> is the addition of design section 3.5. M3 hardens the record without renaming a member.
/// </summary>
/// <param name="GrossJoules">The summed gross energy.</param>
/// <param name="NetJoules">The summed net energy.</param>
/// <param name="InputTokens">The summed prompt tokens.</param>
/// <param name="OutputTokens">The summed completion tokens.</param>
/// <param name="CostByCurrency">The summed cost per currency code.</param>
/// <param name="WallClockMs">The summed wall time of the workflow runs in milliseconds.</param>
/// <param name="Judge">The sums over the judge calls.</param>
/// <param name="Gco2e">The grams of CO2e, estimated with the unit <c>gCO2e</c> when <c>Cost:GCo2ePerKwh</c> is set (an estimated 0 for a measured net total of 0); otherwise N/A with the reason <c>not-configured</c> (no factor), <c>energy-not-measured</c> (net total N/A or not-reported), <c>below-baseline</c> (negative net total) or <c>price-not-representable</c> (product a decimal cannot hold); never null and never a bare 0.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Totals(
    Metric GrossJoules,
    Metric NetJoules,
    Metric InputTokens,
    Metric OutputTokens,
    IReadOnlyDictionary<string, Metric> CostByCurrency,
    Metric WallClockMs,
    JudgeTotals Judge,
    Metric Gco2e);