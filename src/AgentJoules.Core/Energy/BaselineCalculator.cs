using System.Globalization;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Energy;

/// <summary>
/// The one place the idle baseline statistics are computed (design 3.2, REQ-005, EDGE-005). Pure: the same input always gives the same
/// result.
/// </summary>
/// <remarks>
/// <para>
/// The mean is the plain (unweighted) mean of the sample wattages and the deviation is the population standard deviation (divisor n, so
/// the samples 10 W and 20 W give 5 W, not 7.07 W). Both are computed in two passes in <see cref="double"/> arithmetic: the mean first,
/// then the mean of the squared differences from it. There is no running update and no epsilon, so integer samples whose statistics are
/// representable give exact results.
/// </para>
/// <para>
/// The coefficient of variation is the deviation divided by the mean. It is compared with the threshold strictly: a coefficient of
/// variation equal to the threshold is stable and sets no flag. A mean of 0 W or less has no coefficient (no division happens) and no
/// flag, and its mean is not reported; fewer than two samples report neither statistic.
/// </para>
/// </remarks>
public static class BaselineCalculator
{
    private const string WattUnit = "W";
    private const string InsufficientSamples = EnergyReasons.InsufficientSamples;
    private const string MeanNonPositive = "baseline-mean-nonpositive";

    /// <summary>Computes the baseline of the samples of one idle window.</summary>
    /// <param name="samples">The power samples of the window; only their wattages are used.</param>
    /// <param name="startSeconds">The window start in seconds since meter start; finite.</param>
    /// <param name="stopSeconds">The window stop in seconds since meter start; finite and later than the start.</param>
    /// <param name="maxCoefficientOfVariation">The stability threshold; finite and positive.</param>
    /// <returns>The mean, the deviation, the coefficient of variation and the flags; see <see cref="BaselineResult"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="startSeconds"/> or <paramref name="stopSeconds"/> is not finite, the window does not end after it starts, or
    /// <paramref name="maxCoefficientOfVariation"/> is not finite and positive.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A sample wattage is not finite, or the sum, the deviation or the coefficient of variation overflows (the parameter name is
    /// <c>samples</c>).
    /// </exception>
    public static BaselineResult Compute(
        ReadOnlySpan<PowerSample> samples,
        double startSeconds,
        double stopSeconds,
        double maxCoefficientOfVariation)
    {
        // Rejected before the copy so that a bad window or threshold never depends on the samples.
        ValidateWindowAndThreshold(startSeconds, stopSeconds, maxCoefficientOfVariation);

        double[] watts = new double[samples.Length];
        for (int i = 0; i < watts.Length; i++)
        {
            watts[i] = samples[i].Watts;
        }

        return Compute(watts, startSeconds, stopSeconds, maxCoefficientOfVariation, nameof(samples));
    }

    /// <summary>Computes the baseline of the given sample wattages; the statistics of <see cref="Compute(ReadOnlySpan{PowerSample}, double, double, double)"/> without the timestamps.</summary>
    /// <param name="watts">The sample wattages in watts; each finite (a negative one is allowed and gives a mean that is not reported when it drags the mean to 0 or less).</param>
    /// <param name="startSeconds">The window start in seconds since meter start; finite.</param>
    /// <param name="stopSeconds">The window stop in seconds since meter start; finite and later than the start.</param>
    /// <param name="maxCoefficientOfVariation">The stability threshold; finite and positive.</param>
    /// <returns>The mean, the deviation, the coefficient of variation and the flags; see <see cref="BaselineResult"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="startSeconds"/> or <paramref name="stopSeconds"/> is not finite, the window does not end after it starts, or
    /// <paramref name="maxCoefficientOfVariation"/> is not finite and positive.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A wattage is not finite, or the sum, the deviation or the coefficient of variation overflows (the parameter name is
    /// <c>watts</c>).
    /// </exception>
    public static BaselineResult Compute(
        ReadOnlySpan<double> watts,
        double startSeconds,
        double stopSeconds,
        double maxCoefficientOfVariation)
    {
        ValidateWindowAndThreshold(startSeconds, stopSeconds, maxCoefficientOfVariation);

        return Compute(watts, startSeconds, stopSeconds, maxCoefficientOfVariation, nameof(watts));
    }

    private static BaselineResult Compute(
        ReadOnlySpan<double> watts,
        double startSeconds,
        double stopSeconds,
        double maxCoefficientOfVariation,
        string wattsParameterName)
    {
        double seconds = stopSeconds - startSeconds;
        int count = watts.Length;

        double sum = 0.0;
        foreach (double value in watts)
        {
            if (!double.IsFinite(value))
            {
                throw new ArgumentException($"Every sample wattage must be finite, but one is {value.ToString(CultureInfo.InvariantCulture)} W.", wattsParameterName);
            }

            sum += value;
        }

        if (count < 2)
        {
            return new BaselineResult(
                Metric.NotReported(InsufficientSamples),
                Metric.NotReported(InsufficientSamples),
                seconds,
                count,
                null,
                maxCoefficientOfVariation,
                ReadingFlags.None);
        }

        // Pass one: the mean. Pass two: the mean of the squared differences from it (divisor n: the population variance).
        double mean = sum / count;
        double squares = 0.0;
        foreach (double value in watts)
        {
            double difference = value - mean;
            squares += difference * difference;
        }

        double standardDeviation = Math.Sqrt(squares / count);
        if (!double.IsFinite(mean) || !double.IsFinite(standardDeviation))
        {
            throw new ArgumentException("The baseline statistics overflow: the sample wattages are too large.", wattsParameterName);
        }

        Metric deviation = Metric.Measured(standardDeviation, WattUnit);
        if (mean <= 0.0)
        {
            // No coefficient of variation exists for a mean of 0 W or less, and the baseline is not usable: no division, no flag.
            return new BaselineResult(
                Metric.NotReported(MeanNonPositive),
                deviation,
                seconds,
                count,
                null,
                maxCoefficientOfVariation,
                ReadingFlags.None);
        }

        double coefficientOfVariation = standardDeviation / mean;
        if (!double.IsFinite(coefficientOfVariation))
        {
            // A mean so close to 0 W that the quotient overflows: no finite coefficient exists, and an infinite one cannot be printed or serialised.
            throw new ArgumentException("The baseline statistics overflow: the coefficient of variation is not finite.", wattsParameterName);
        }

        // Strictly greater: a coefficient of variation equal to the threshold is stable.
        ReadingFlags flags = coefficientOfVariation > maxCoefficientOfVariation ? ReadingFlags.BaselineUnstable : ReadingFlags.None;

        return new BaselineResult(
            Metric.Measured(mean, WattUnit),
            deviation,
            seconds,
            count,
            coefficientOfVariation,
            maxCoefficientOfVariation,
            flags);
    }

    private static void ValidateWindowAndThreshold(double startSeconds, double stopSeconds, double maxCoefficientOfVariation)
    {
        if (!double.IsFinite(startSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds), startSeconds, "The baseline window start must be finite.");
        }

        if (!double.IsFinite(stopSeconds) || !double.IsFinite(stopSeconds - startSeconds) || stopSeconds <= startSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stopSeconds),
                stopSeconds,
                $"The baseline window must be finite and end after it starts, but it is {startSeconds.ToString(CultureInfo.InvariantCulture)} to {stopSeconds.ToString(CultureInfo.InvariantCulture)} s.");
        }

        if (!double.IsFinite(maxCoefficientOfVariation) || maxCoefficientOfVariation <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCoefficientOfVariation),
                maxCoefficientOfVariation,
                "The coefficient-of-variation threshold must be finite and positive.");
        }
    }
}