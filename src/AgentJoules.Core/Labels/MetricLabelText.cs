namespace AgentJoules.Core.Labels;

/// <summary>
/// The text of each <see cref="MetricLabel"/>, as the results schema fixes it and as every surface prints it: the JSON converter writes these strings and the console prints them for a value
/// that does not exist, so a file and a terminal can never spell a label differently. They equal the <c>JsonStringEnumMemberName</c> attributes on <see cref="MetricLabel"/>.
/// </summary>
public static class MetricLabelText
{
    /// <summary>The text of <see cref="MetricLabel.Measured"/>.</summary>
    public const string Measured = "measured";

    /// <summary>The text of <see cref="MetricLabel.Estimated"/>.</summary>
    public const string Estimated = "estimated";

    /// <summary>The text of <see cref="MetricLabel.ModelJudged"/>.</summary>
    public const string ModelJudged = "model-judged";

    /// <summary>The text of <see cref="MetricLabel.NotApplicable"/>.</summary>
    public const string NotApplicable = "N/A";

    /// <summary>The text of <see cref="MetricLabel.NotReported"/>.</summary>
    public const string NotReported = "not-reported";

    /// <summary>The text of <paramref name="label"/>.</summary>
    /// <param name="label">A defined label.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="label"/> is not a defined label.</exception>
    public static string Of(MetricLabel label) => label switch
    {
        MetricLabel.Measured => Measured,
        MetricLabel.Estimated => Estimated,
        MetricLabel.ModelJudged => ModelJudged,
        MetricLabel.NotApplicable => NotApplicable,
        MetricLabel.NotReported => NotReported,
        _ => throw new ArgumentOutOfRangeException(nameof(label), (int)label, "The label is not a defined metric label."),
    };
}