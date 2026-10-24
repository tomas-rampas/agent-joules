using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Quality;

/// <summary>
/// The two metrics derived from the judge quality of one step (REQ-010, design 3.2): joules per quality point and cost per quality point. A derived value is computed only when both of its inputs
/// exist; otherwise it is N/A with a reason, and a missing input is never written as 0. A derived value is a judgement-based ratio, so it is labelled <see cref="MetricLabel.ModelJudged"/>
/// and never measured or estimated. The functions take the scalars of one step, so nothing is computed across steps or across tiers: the caller (<c>RunAssembler.WithDerivedMetrics</c>) applies
/// them to each step on its own.
/// </summary>
public static class DerivedMetrics
{
    /// <summary>The reason of a derived metric whose quality, energy or cost has no value.</summary>
    public const string MissingInputReason = "missing-input";

    /// <summary>The reason of joules per quality point on a step whose net energy is below the idle baseline, so it is negative and no per-point ratio is meaningful (AJ-072 treats the same step the same way).</summary>
    public const string BelowBaselineReason = "below-baseline";

    /// <summary>
    /// Joules per quality point: the net energy divided by the quality, a model-judged value without a unit. The checks run in this order: the quality has a model-judged value, the net energy has a value,
    /// the step is not flagged <see cref="ReadingFlags.BelowBaseline"/>; the first that fails gives N/A with <see cref="MissingInputReason"/> or <see cref="BelowBaselineReason"/>.
    /// </summary>
    /// <param name="netJoules">The net energy of the step; a hosted step holds N/A with the reason <c>hosted-tier</c>.</param>
    /// <param name="quality">The quality of the step: model-judged when scored, N/A or not-reported otherwise.</param>
    /// <param name="flags">The reading flags of the step.</param>
    /// <returns>A model-judged metric, or N/A.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="netJoules"/> or <paramref name="quality"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The quality holds a score below 1 or not finite (the judge parser only produces 1 to 5). With a score of at least 1 the quotient is never larger in magnitude than its input, so it cannot overflow.</exception>
    public static Metric JoulesPerQualityPoint(Metric netJoules, Metric quality, ReadingFlags flags = ReadingFlags.None)
    {
        ArgumentNullException.ThrowIfNull(netJoules);
        ArgumentNullException.ThrowIfNull(quality);

        return JudgedValue(quality) is not { } score || netJoules.Value is not { } joules
            ? Metric.NotApplicable(MissingInputReason)
            : flags.HasFlag(ReadingFlags.BelowBaseline)
                ? Metric.NotApplicable(BelowBaselineReason)
                : Metric.ModelJudged(joules / score);
    }

    /// <summary>
    /// Cost per quality point: the cost divided by the quality, a model-judged value in the currency of the cost. N/A with <see cref="MissingInputReason"/> when the quality or the cost has no value.
    /// The below-baseline flag does not apply: it concerns energy only.
    /// </summary>
    /// <param name="cost">The cost of the step, measured or estimated with a currency; N/A or not-reported when there is none.</param>
    /// <param name="quality">The quality of the step: model-judged when scored, N/A or not-reported otherwise.</param>
    /// <returns>A model-judged metric with the currency of the cost, or N/A.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cost"/> or <paramref name="quality"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The quality holds a score below 1 or not finite (see <see cref="JoulesPerQualityPoint"/>).</exception>
    /// <exception cref="ArgumentException">The cost holds a value but no currency (a programmer error: a cost always names its currency).</exception>
    public static Metric CostPerQualityPoint(Metric cost, Metric quality)
    {
        ArgumentNullException.ThrowIfNull(cost);
        ArgumentNullException.ThrowIfNull(quality);

        return JudgedValue(quality) is not { } score || cost.Value is not { } amount
            ? Metric.NotApplicable(MissingInputReason)
            : string.IsNullOrWhiteSpace(cost.Currency)
                ? throw new ArgumentException("A cost with a value must carry a currency.", nameof(cost))
                : Metric.ModelJudgedCost(amount / score, cost.Currency);
    }

    // The score of a model-judged quality, or null when the quality is absent or is not a judge score; a score below 1 (or not finite) is a programmer error, because the parser only produces 1 to 5.
    private static double? JudgedValue(Metric quality)
    {
        return quality is { Label: MetricLabel.ModelJudged, Value: { } score }
            ? double.IsFinite(score) && score >= 1.0
                ? score
                : throw new ArgumentOutOfRangeException(nameof(quality), score, "A quality score must be a finite number of at least 1.")
            : null;
    }
}