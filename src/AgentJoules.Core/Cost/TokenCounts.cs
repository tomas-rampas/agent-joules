using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Cost;

/// <summary>
/// The token counts of one step as a provider reported them (REQ-008, EDGE-007): each count is a <see cref="Metric"/> that is measured when the
/// provider stated a number (a stated 0 included: the label, not the number, decides) and not-reported when it did not, never a 0 invented
/// for a missing count. <see cref="From"/> is the one place that rule lives; the decorator reads the two numeric fields of the provider's
/// usage and nothing else, so no provider object travels past it.
/// </summary>
public sealed record TokenCounts
{
    /// <summary>The unit of a token metric.</summary>
    public const string TokenUnit = "tokens";

    /// <summary>The reason of a count the provider left out.</summary>
    public const string OmittedReason = "provider-omitted-usage";

    /// <summary>The reason of a count the provider stated as a negative number, which no tokenizer produces and which must not become a measurement.</summary>
    public const string InvalidReason = "provider-invalid-usage";

    private TokenCounts(Metric inputTokens, Metric outputTokens, long? inputCount, long? outputCount)
    {
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        InputCount = inputCount;
        OutputCount = outputCount;
    }

    /// <summary>Neither count was reported (the provider sent no usage).</summary>
    public static TokenCounts NotReported { get; } = From(null, null);

    /// <summary>The prompt tokens: measured, or not-reported with a reason.</summary>
    public Metric InputTokens { get; }

    /// <summary>The completion tokens: measured, or not-reported with a reason.</summary>
    public Metric OutputTokens { get; }

    /// <summary>The prompt token count the provider stated, or null when <see cref="InputTokens"/> is not-reported; the raw number a price needs.</summary>
    public long? InputCount { get; }

    /// <summary>The completion token count the provider stated, or null when <see cref="OutputTokens"/> is not-reported; the raw number a price needs.</summary>
    public long? OutputCount { get; }

    /// <summary>Maps the two counts a provider reported, each independently: a number is measured (0 stays 0), null is not-reported, a negative number is not-reported as invalid.</summary>
    /// <param name="inputTokenCount">The stated prompt tokens, or null when the provider left them out.</param>
    /// <param name="outputTokenCount">The stated completion tokens, or null when the provider left them out.</param>
    /// <returns>The counts.</returns>
    public static TokenCounts From(long? inputTokenCount, long? outputTokenCount)
    {
        (Metric input, long? inputCount) = Map(inputTokenCount);
        (Metric output, long? outputCount) = Map(outputTokenCount);
        return new TokenCounts(input, output, inputCount, outputCount);
    }

    /// <summary>True when the completion tokens were reported and exceed the limit of the task; false when either is absent (an unreported count never raises the flag).</summary>
    /// <param name="maxOutputTokens">The output limit of the task, or null when it has none.</param>
    /// <returns>Whether the step went past its limit.</returns>
    public bool ExceedsOutputLimit(int? maxOutputTokens) => OutputCount is { } produced && maxOutputTokens is { } limit && produced > limit;

    private static (Metric Metric, long? Count) Map(long? stated) => stated switch
    {
        null => (Metric.NotReported(OmittedReason), null),
        < 0 => (Metric.NotReported(InvalidReason), null),
        { } count => (Metric.Measured(count, TokenUnit), count),
    };
}