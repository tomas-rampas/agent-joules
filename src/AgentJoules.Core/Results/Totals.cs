using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Results;

/// <summary>
/// The <c>totals</c> object of the results file (REQ-015): the sums over the steps of the workflow, judge calls excluded (they are in
/// <see cref="Judge"/>). Every number is a label object, so a sum that cannot be formed is N/A or not-reported, never 0. The names are the camelCase
/// names of the prompt skeleton; <see cref="Gco2e"/> is the addition of design section 3.5. Every member is get-only, and the constructor checks the cost map (see <see cref="CostByCurrency"/>).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Totals
{
    private const string CostParameter = "CostByCurrency";

    /// <summary>Creates the totals after checking the cost map.</summary>
    /// <param name="grossJoules">The summed gross energy.</param>
    /// <param name="netJoules">The summed net energy.</param>
    /// <param name="inputTokens">The summed prompt tokens.</param>
    /// <param name="outputTokens">The summed completion tokens.</param>
    /// <param name="costByCurrency">The summed cost per currency code; every entry is estimated in the currency of its key, or N/A.</param>
    /// <param name="wallClockMs">The summed wall time of the workflow runs in milliseconds.</param>
    /// <param name="judge">The sums over the judge calls.</param>
    /// <param name="gco2e">The grams of CO2e, estimated with the unit <c>gCO2e</c> when <c>Cost:GCo2ePerKwh</c> is set (an estimated 0 for a measured net total of 0); otherwise N/A with the reason <c>not-configured</c> (no factor), <c>energy-not-measured</c> (net total N/A or not-reported), <c>below-baseline</c> (negative net total) or <c>price-not-representable</c> (product a decimal cannot hold); never null and never a bare 0.</param>
    /// <exception cref="ArgumentNullException"><paramref name="costByCurrency"/> is null.</exception>
    /// <exception cref="ArgumentException">A cost entry has a blank key, is null, or is neither N/A nor an estimate without a unit, finite, not negative, in the currency of its key (compared ordinally).</exception>
    [JsonConstructor]
    public Totals(Metric grossJoules, Metric netJoules, Metric inputTokens, Metric outputTokens, IReadOnlyDictionary<string, Metric> costByCurrency, Metric wallClockMs, JudgeTotals judge, Metric gco2e)
    {
        GrossJoules = grossJoules;
        NetJoules = netJoules;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CostByCurrency = RequireCostMap("totals.costByCurrency", costByCurrency);
        WallClockMs = wallClockMs;
        Judge = judge;
        Gco2e = gco2e;
    }

    /// <summary>The summed gross energy.</summary>
    public Metric GrossJoules { get; }

    /// <summary>The summed net energy.</summary>
    public Metric NetJoules { get; }

    /// <summary>The summed prompt tokens.</summary>
    public Metric InputTokens { get; }

    /// <summary>The summed completion tokens.</summary>
    public Metric OutputTokens { get; }

    /// <summary>
    /// The summed cost per currency code, no conversion. The sums are taken in double in run order per ordinal currency key; the map is empty when no step carries a cost (each step keeps its N/A reason);
    /// each entry is estimated in the currency of its key, or N/A.
    /// </summary>
    public IReadOnlyDictionary<string, Metric> CostByCurrency { get; }

    /// <summary>The summed wall time of the workflow runs in milliseconds.</summary>
    public Metric WallClockMs { get; }

    /// <summary>The sums over the judge calls.</summary>
    public JudgeTotals Judge { get; }

    /// <summary>The grams of CO2e (see the constructor for the reasons of an N/A value).</summary>
    public Metric Gco2e { get; }

    /// <summary>
    /// Checks the per-entry invariant of a cost map: the key is not blank, and the value is N/A, or estimated with a currency equal (ordinally) to the key, no unit, and a finite amount that is not negative.
    /// The messages name the member and the (bounded) key, never an amount.
    /// </summary>
    /// <param name="member">The path of the map in the file, for the messages.</param>
    /// <param name="costs">The map.</param>
    /// <returns>A read-only copy of the map, in the enumeration order of <paramref name="costs"/> (a caller that keeps the original cannot change what is stored).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="costs"/> is null.</exception>
    /// <exception cref="ArgumentException">An entry breaks the invariant (parameter name <c>CostByCurrency</c>).</exception>
    internal static IReadOnlyDictionary<string, Metric> RequireCostMap(string member, IReadOnlyDictionary<string, Metric>? costs)
    {
        ArgumentNullException.ThrowIfNull(costs, CostParameter);

        // Copy first and check the copy, so the stored map is the checked one; a Dictionary keeps the insertion order (a frozen one would not).
        var copy = new Dictionary<string, Metric>(costs, StringComparer.Ordinal);
        foreach ((string key, Metric? entry) in copy)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw Rejected(CostParameter, $"{member} holds an entry with a blank currency key.");
            }

            string name = $"{member}[{TextEcho.ForMessage(key)}]";
            if (entry is null)
            {
                throw Rejected(CostParameter, $"{name} is null.");
            }

            if (entry.Label == MetricLabel.NotApplicable)
            {
                continue;
            }

            if (entry.Label != MetricLabel.Estimated || entry.Unit is not null || !string.Equals(entry.Currency, key, StringComparison.Ordinal))
            {
                throw Rejected(CostParameter, $"{name} must be N/A or an estimate in the currency of its key, without a unit.");
            }

            if (entry.Value is not { } amount || !double.IsFinite(amount) || amount < 0.0)
            {
                throw Rejected(CostParameter, $"{name} must be a finite amount that is not negative.");
            }
        }

        return new ReadOnlyDictionary<string, Metric>(copy);
    }

    private static ArgumentException Rejected(string parameterName, string message) => new(message, parameterName);
}