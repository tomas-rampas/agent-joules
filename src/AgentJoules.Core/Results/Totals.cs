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
/// <param name="Gco2e">The estimated grams of CO2e; N/A with the reason <c>not-configured</c> when no carbon factor is set, never null and never 0.</param>
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