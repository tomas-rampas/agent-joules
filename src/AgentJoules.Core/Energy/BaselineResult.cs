using System.Globalization;
using System.Text.Json.Serialization;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Energy;

/// <summary>
/// The idle baseline of one measurement: the mean and the population standard deviation of the power samples collected while the GPU
/// was idle, and whether that baseline is stable enough to subtract (design 3.2, REQ-005, EDGE-005). In production
/// <see cref="BaselineCalculator"/> builds it; tests and deserialisation build it through the same public constructor, which checks every
/// invariant, so a result that exists is always well formed.
/// </summary>
public sealed record BaselineResult
{
    private const string WattUnit = "W";
    private const string InsufficientSamples = EnergyReasons.InsufficientSamples;
    private const string MeanNonPositive = "baseline-mean-nonpositive";

    /// <summary>Creates a result after checking every invariant.</summary>
    /// <param name="meanW">
    /// The unweighted mean of the sample wattages: <c>measured</c> in W, or <c>not-reported</c> with the reason
    /// <c>insufficient-samples</c> (fewer than two samples) or <c>baseline-mean-nonpositive</c> (a mean of 0 W or less). A not-reported
    /// mean is never used as a baseline: no net value is computed against it.
    /// </param>
    /// <param name="stdDevW">
    /// The population standard deviation (divisor n, not n - 1) of the sample wattages: <c>measured</c> in W whenever there are at least
    /// two samples, including when the mean is not positive, and <c>not-reported</c> with the reason <c>insufficient-samples</c> otherwise.
    /// </param>
    /// <param name="seconds">The length of the baseline window: stop minus start, in seconds; positive and finite.</param>
    /// <param name="sampleCount">The number of samples the statistics were computed over (0 and 1 included); not negative.</param>
    /// <param name="coefficientOfVariation">
    /// The standard deviation divided by the mean, or <c>null</c> exactly when the mean is not measured (never a division by zero); finite
    /// and not negative otherwise, 0 for a positive mean and a zero deviation.
    /// </param>
    /// <param name="maxCoefficientOfVariation">The threshold the coefficient of variation was compared with (<c>Baseline:MaxCoefficientOfVariation</c>); positive and finite.</param>
    /// <param name="flags">
    /// <see cref="ReadingFlags.BaselineUnstable"/> exactly when the coefficient of variation exists and is strictly greater than
    /// <paramref name="maxCoefficientOfVariation"/>; otherwise <see cref="ReadingFlags.None"/>, also for every not-reported baseline. No
    /// other flag is allowed.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="meanW"/> or <paramref name="stdDevW"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="seconds"/>, <paramref name="sampleCount"/>, <paramref name="maxCoefficientOfVariation"/> or
    /// <paramref name="coefficientOfVariation"/> is outside the range named above.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A metric is neither measured nor not-reported, or is measured without the unit W; a measured mean is not greater than 0 or a
    /// measured deviation is negative; a not-reported reason is outside the vocabulary (<c>insufficient-samples</c> or
    /// <c>baseline-mean-nonpositive</c> for the mean, <c>insufficient-samples</c> for the deviation); the sample count is below 2 without
    /// both metrics not-reported <c>insufficient-samples</c>, or 2 or more with either; the coefficient of variation is null while the
    /// mean is measured or present while it is not; or the flags are not exactly the ones the coefficient of variation and the threshold
    /// imply.
    /// </exception>
    [JsonConstructor]
    public BaselineResult(
        Metric meanW,
        Metric stdDevW,
        double seconds,
        int sampleCount,
        double? coefficientOfVariation,
        double maxCoefficientOfVariation,
        ReadingFlags flags)
    {
        ArgumentNullException.ThrowIfNull(meanW);
        ArgumentNullException.ThrowIfNull(stdDevW);

        if (!double.IsFinite(seconds) || seconds <= 0.0)
        {
            throw OutOfRange(nameof(seconds), seconds, "The baseline seconds must be positive and finite.");
        }

        if (sampleCount < 0)
        {
            throw OutOfRange(nameof(sampleCount), sampleCount, "The baseline sample count must not be negative.");
        }

        if (!double.IsFinite(maxCoefficientOfVariation) || maxCoefficientOfVariation <= 0.0)
        {
            throw OutOfRange(nameof(maxCoefficientOfVariation), maxCoefficientOfVariation, "The coefficient-of-variation threshold must be positive and finite.");
        }

        if (coefficientOfVariation is { } cv && (!double.IsFinite(cv) || cv < 0.0))
        {
            throw OutOfRange(nameof(coefficientOfVariation), cv, "The coefficient of variation must be finite and not negative.");
        }

        RequireShape(nameof(meanW), meanW);
        RequireShape(nameof(stdDevW), stdDevW);

        // A usable mean is positive; a mean of 0 W or less is reported as not-reported, never as a measured value. A deviation is never negative.
        if (meanW is { Label: MetricLabel.Measured, Value: <= 0.0 })
        {
            throw new ArgumentException("A measured mean must be greater than 0 W; a mean of 0 W or less is not reported.", nameof(meanW));
        }

        if (stdDevW is { Label: MetricLabel.Measured, Value: < 0.0 })
        {
            throw new ArgumentException("A measured standard deviation must not be negative.", nameof(stdDevW));
        }

        RequireReason(nameof(meanW), meanW, InsufficientSamples, MeanNonPositive);
        RequireReason(nameof(stdDevW), stdDevW, InsufficientSamples);

        // Fewer than two samples have no statistics, and two or more always have a deviation: the count and the metrics agree.
        bool meanInsufficient = meanW.Reason == InsufficientSamples;
        bool deviationInsufficient = stdDevW.Reason == InsufficientSamples;
        if ((sampleCount < 2) != (meanInsufficient && deviationInsufficient) || meanInsufficient != deviationInsufficient)
        {
            throw new ArgumentException(
                $"A sample count below 2 requires both metrics not-reported with the reason {InsufficientSamples}, and a count of 2 or more allows neither, but the count is {sampleCount}.",
                nameof(sampleCount));
        }

        bool meanMeasured = meanW.Label == MetricLabel.Measured;
        if (meanMeasured != coefficientOfVariation.HasValue)
        {
            throw new ArgumentException(
                meanMeasured
                    ? "A measured mean requires a coefficient of variation."
                    : "A mean that is not reported has no coefficient of variation.",
                nameof(coefficientOfVariation));
        }

        ReadingFlags expected = coefficientOfVariation > maxCoefficientOfVariation ? ReadingFlags.BaselineUnstable : ReadingFlags.None;
        if (flags != expected)
        {
            throw new ArgumentException(
                $"The flags must be exactly {(expected == ReadingFlags.None ? "none" : nameof(ReadingFlags.BaselineUnstable))}: {nameof(ReadingFlags.BaselineUnstable)} is set when the coefficient of variation is strictly greater than the threshold and nothing else is allowed.",
                nameof(flags));
        }

        MeanW = meanW;
        StdDevW = stdDevW;
        Seconds = seconds;
        SampleCount = sampleCount;
        CoefficientOfVariation = coefficientOfVariation;
        MaxCoefficientOfVariation = maxCoefficientOfVariation;
        Flags = flags;
    }

    /// <summary>The unweighted mean of the sample wattages, or not-reported with <c>insufficient-samples</c> or <c>baseline-mean-nonpositive</c>.</summary>
    public Metric MeanW { get; }

    /// <summary>The population standard deviation of the sample wattages, or not-reported with <c>insufficient-samples</c>.</summary>
    public Metric StdDevW { get; }

    /// <summary>The length of the baseline window in seconds: stop minus start.</summary>
    public double Seconds { get; }

    /// <summary>
    /// The configured <c>Baseline:SettleSeconds</c>, the wait between the warm-up and the baseline window, in seconds: a configuration echo, 0 when none was configured and in files written before it existed.
    /// Not part of the window <see cref="Seconds"/>; set by the run flow, not by the calculator.
    /// </summary>
    public double SettleSeconds { get; init; }

    /// <summary>The number of samples the statistics were computed over.</summary>
    public int SampleCount { get; }

    /// <summary>The standard deviation divided by the mean, or null when the mean is not reported.</summary>
    public double? CoefficientOfVariation { get; }

    /// <summary>The threshold the coefficient of variation was compared with.</summary>
    public double MaxCoefficientOfVariation { get; }

    /// <summary>
    /// <see cref="ReadingFlags.BaselineUnstable"/> or none, serialized as an array of vocabulary strings. A property-level converter
    /// outranks a <see cref="JsonStringEnumConverter"/> in the caller's options.
    /// </summary>
    [JsonConverter(typeof(ReadingFlagsJsonConverter))]
    public ReadingFlags Flags { get; }

    private static ArgumentOutOfRangeException OutOfRange(string parameter, double actual, string message) =>
        new(parameter, actual.ToString(CultureInfo.InvariantCulture), message);

    // A not-reported metric of a baseline has one of the listed reasons; a measured metric has none.
    private static void RequireReason(string parameter, Metric metric, params string[] allowed)
    {
        if (metric.Label == MetricLabel.NotReported && Array.IndexOf(allowed, metric.Reason) < 0)
        {
            throw new ArgumentException(
                $"A not-reported metric of a baseline must have the reason {string.Join(" or ", allowed)}, but it has {(metric.Reason is null ? "none" : TextEcho.ForMessage(metric.Reason))}.",
                parameter);
        }
    }

    // A baseline holds a measured quantity in W or an explicit absence; any other label does not belong in it.
    private static void RequireShape(string parameter, Metric metric)
    {
        if (metric.Label is not (MetricLabel.Measured or MetricLabel.NotReported))
        {
            throw new ArgumentException($"The metric must be measured or not-reported, but it is {metric.Label}.", parameter);
        }

        if (metric.Label == MetricLabel.Measured && !string.Equals(metric.Unit, WattUnit, StringComparison.Ordinal))
        {
            throw new ArgumentException($"A measured metric must carry the unit {WattUnit}, but it carries {(metric.Unit is null ? "none" : TextEcho.ForMessage(metric.Unit))}.", parameter);
        }
    }
}