using System.Text.Json.Serialization;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Results;

/// <summary>
/// The <c>totals.judge</c> object: the judge calls are summed apart from the workflow, so the workflow figures never include them. Every member is get-only, and the constructor checks the cost map
/// with the rule of <see cref="Totals.CostByCurrency"/>.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JudgeTotals
{
    /// <summary>Creates the judge totals after checking the cost map.</summary>
    /// <param name="inputTokens">The prompt tokens of all judge calls.</param>
    /// <param name="outputTokens">The completion tokens of all judge calls.</param>
    /// <param name="costByCurrency">The judge cost per currency code; every entry is estimated in the currency of its key, or N/A.</param>
    /// <exception cref="ArgumentNullException"><paramref name="costByCurrency"/> is null.</exception>
    /// <exception cref="ArgumentException">A cost entry breaks the rule of <see cref="Totals.CostByCurrency"/> (parameter name <c>CostByCurrency</c>).</exception>
    [JsonConstructor]
    public JudgeTotals(Metric inputTokens, Metric outputTokens, IReadOnlyDictionary<string, Metric> costByCurrency)
    {
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CostByCurrency = Totals.RequireCostMap("totals.judge.costByCurrency", costByCurrency);
    }

    /// <summary>The prompt tokens of all judge calls.</summary>
    public Metric InputTokens { get; }

    /// <summary>The completion tokens of all judge calls.</summary>
    public Metric OutputTokens { get; }

    /// <summary>The judge cost per currency code.</summary>
    public IReadOnlyDictionary<string, Metric> CostByCurrency { get; }
}