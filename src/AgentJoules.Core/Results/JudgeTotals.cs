using System.Text.Json.Serialization;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Results;

/// <summary>The <c>totals.judge</c> object: the judge calls are summed apart from the workflow, so the workflow figures never include them.</summary>
/// <param name="InputTokens">The prompt tokens of all judge calls.</param>
/// <param name="OutputTokens">The completion tokens of all judge calls.</param>
/// <param name="CostByCurrency">The judge cost per currency code.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JudgeTotals(
    Metric InputTokens,
    Metric OutputTokens,
    IReadOnlyDictionary<string, Metric> CostByCurrency);