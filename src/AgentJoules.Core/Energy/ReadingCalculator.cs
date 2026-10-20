using System.Globalization;
using System.Runtime.InteropServices;

using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Energy;

/// <summary>
/// The one place gross, net, average and peak energy, the flags and the honesty labels of a window are computed (design 3.2).
/// Pure: the same input always gives the same reading.
/// </summary>
public static class ReadingCalculator
{
    private const string JouleUnit = "J";
    private const string WattUnit = "W";
    private const string InsufficientSamples = "insufficient-samples";
    private const string CounterNotRead = "counter-not-read";
    private const string CounterWentBackwards = "counter-went-backwards";
    private const string NoBaseline = "no-baseline";

    /// <summary>Computes the reading of one window.</summary>
    /// <param name="input">The samples, window, method, counter delta, baseline and rate.</param>
    /// <returns>
    /// The reading. Gross is the counter delta over 1000 for the counter method (not-reported when the delta is missing or negative)
    /// and the sampling integral for the sampling method. Average, peak and sample count always come from the samples.
    /// Net is gross minus baseline times duration, kept when negative and flagged below-baseline.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The parameter name is <c>input</c> and the message names the field. The sample list is null; the window is not finite or does not
    /// end after it starts; the sample rate is not positive and finite; the baseline is negative, not finite or overflows when multiplied
    /// by the duration; the method is undefined; the sampling-fallback flag is set with a method other than sampling; a counter delta is
    /// given with the sampling method; or a sample is invalid for the window (see <see cref="PowerIntegrator"/>).
    /// </exception>
    public static EnergyReading Compute(ReadingInput input)
    {
        IReadOnlyList<PowerSample> list = input.Samples
            ?? throw new ArgumentException("ReadingInput.Samples must not be null.", nameof(input));
        double start = input.WindowStartSeconds;
        double stop = input.WindowStopSeconds;
        double duration = stop - start;
        Validate(input, duration);

        // One snapshot serves the integral, the average, the peak and the count, so they always describe the same samples.
        ReadOnlySpan<PowerSample> samples = Snapshot(list);
        double? integral = PowerIntegrator.IntegrateJoules(samples, start, stop);
        double? average = PowerIntegrator.AverageWatts(samples, start, stop);
        double? peak = PowerIntegrator.PeakWatts(samples, start, stop);

        ReadingFlags flags = ReadingFlags.None;
        if (input.IsSamplingFallback)
        {
            flags |= ReadingFlags.SamplingFallback;
        }

        // Compared in integer ticks so float noise in a computed window never flips a window of exactly 2 / rate.
        if (ToTicks(duration) < ToTicks(2.0 / input.SampleRateHz))
        {
            flags |= ReadingFlags.LowResolution;
        }

        // A power or thermal reason in any sample, an edge included, flags the window and names it (a positive claim needs one sample).
        // An empty list ("nothing was active") is a negative claim and needs every sample supported; otherwise the list is null and
        // no throttled claim is made either way (EDGE-013).
        ThrottleReasons throttle = ThrottleReasons.Unsupported;
        bool everySampleSupported = samples.Length > 0;
        foreach (PowerSample sample in samples)
        {
            throttle = throttle.Or(sample.Reasons);
            everySampleSupported &= sample.Reasons.Supported;
        }

        if (throttle.IsThrottled)
        {
            flags |= ReadingFlags.Throttled;
        }

        Metric gross = GrossOf(input, integral);
        Metric net = NetOf(gross, input.BaselineMeanWatts, duration, ref flags);

        return new EnergyReading(
            grossJoules: gross,
            netJoules: net,
            averageWatts: Watts(average),
            peakWatts: Watts(peak),
            durationSeconds: duration,
            method: input.Method,
            sampleCount: samples.Length,
            sampleRateHz: input.SampleRateHz,
            flags: flags,
            throttleReasons: throttle.ReadingNames(everySampleSupported));
    }

    private static void Validate(ReadingInput input, double duration)
    {
        if (!double.IsFinite(input.WindowStartSeconds) || !double.IsFinite(input.WindowStopSeconds) || !double.IsFinite(duration) || duration <= 0.0)
        {
            throw Rejected(nameof(input), $"ReadingInput window must be finite and end after it starts, but it is {input.WindowStartSeconds} to {input.WindowStopSeconds} s.");
        }

        if (!double.IsFinite(input.SampleRateHz) || input.SampleRateHz <= 0.0)
        {
            throw Rejected(nameof(input), $"ReadingInput.SampleRateHz must be positive and finite, but it is {input.SampleRateHz} Hz.");
        }

        // A baseline is an idle power: never negative. The product with the duration must stay finite so net cannot overflow.
        if (input.BaselineMeanWatts is { } baseline && (!double.IsFinite(baseline) || baseline < 0.0 || !double.IsFinite(baseline * duration)))
        {
            throw Rejected(nameof(input), $"ReadingInput.BaselineMeanWatts must be finite, not negative and finite when multiplied by the {duration} s duration, but it is {baseline} W.");
        }

        if (!Enum.IsDefined(input.Method))
        {
            throw Rejected(nameof(input), $"ReadingInput.Method {(int)input.Method} is not a defined energy method.");
        }

        // A hosted tier has no samples to compute from; its readings come from UnmeteredEnergyMeter, never from this calculator.
        if (input.Method == EnergyMethod.Unmetered)
        {
            throw Rejected(nameof(input), $"ReadingInput.Method must be counter or sampling, but it is {input.Method}.");
        }

        if (input.IsSamplingFallback && input.Method != EnergyMethod.Sampling)
        {
            throw Rejected(nameof(input), $"ReadingInput.IsSamplingFallback requires the sampling method, but the method is {input.Method}.");
        }

        if (input.Method == EnergyMethod.Sampling && input.CounterDeltaMillijoules is not null)
        {
            throw Rejected(nameof(input), $"ReadingInput.CounterDeltaMillijoules must be null with the sampling method, but it is {input.CounterDeltaMillijoules} mJ.");
        }
    }

    // The parameter of Compute is "input"; the message names the offending field.
    private static ArgumentException Rejected(string parameterName, FormattableString message) =>
        new(message.ToString(CultureInfo.InvariantCulture), parameterName);

    // Arrays and List<T> are viewed in place; any other list is copied once. Either way the length below is read from the snapshot.
    private static ReadOnlySpan<PowerSample> Snapshot(IReadOnlyList<PowerSample> samples) => samples switch
    {
        PowerSample[] array => array,
        List<PowerSample> list => CollectionsMarshal.AsSpan(list),
        _ => CopyOf(samples),
    };

    private static PowerSample[] CopyOf(IReadOnlyList<PowerSample> samples)
    {
        PowerSample[] copy = new PowerSample[samples.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = samples[i];
        }

        return copy;
    }

    private static Metric GrossOf(ReadingInput input, double? integral) =>
        input.Method == EnergyMethod.Sampling ? SamplingGross(integral) : CounterGross(input.CounterDeltaMillijoules);

    private static Metric SamplingGross(double? integral) =>
        integral is { } joules ? Metric.Measured(joules, JouleUnit) : Metric.NotReported(InsufficientSamples);

    private static Metric CounterGross(long? deltaMillijoules) => deltaMillijoules switch
    {
        null => Metric.NotReported(CounterNotRead),
        < 0 => Metric.NotReported(CounterWentBackwards),
        long delta => Metric.Measured(delta / 1000.0, JouleUnit),
    };

    private static Metric NetOf(Metric gross, double? baselineWatts, double duration, ref ReadingFlags flags)
    {
        // Not-reported gross has no value; its reason is the reason net is missing too, whether or not a baseline exists.
        if (gross.Value is not { } grossJoules)
        {
            return Metric.NotReported(gross.Reason!);
        }

        if (baselineWatts is not { } baseline)
        {
            return Metric.NotReported(NoBaseline);
        }

        // Never clamped (REQ-005): a negative net is published together with the below-baseline flag, whatever its size.
        double netJoules = grossJoules - (baseline * duration);
        if (netJoules < 0.0)
        {
            flags |= ReadingFlags.BelowBaseline;
        }

        return Metric.Measured(netJoules, JouleUnit);
    }

    private static Metric Watts(double? watts) =>
        watts is { } value ? Metric.Measured(value, WattUnit) : Metric.NotReported(InsufficientSamples);

    // Double-to-long casts saturate since .NET 9, so the branch is a guard that keeps the intent explicit (a tiny sample rate gives
    // the largest threshold) and keeps the result independent of the cast semantics of the runtime.
    private static long ToTicks(double seconds)
    {
        double ticks = Math.Round(seconds * TimeSpan.TicksPerSecond);
        return ticks >= long.MaxValue ? long.MaxValue : (long)ticks;
    }
}