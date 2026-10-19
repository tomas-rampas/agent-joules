using System.Globalization;
using System.Runtime.InteropServices;

namespace AgentJoules.Core.Energy;

/// <summary>
/// Turns power samples into joules (design 3.2). The integral is the trapezoid between consecutive samples
/// at their actual timestamps, never a nominal interval; the first sample holds its watts from the window
/// start to its timestamp and the last sample holds its watts from its timestamp to the window stop.
/// A result of <c>null</c> means no value (fewer than two samples): the caller labels it not-reported, never 0.
/// All three methods share one validated contract: the window first, then each sample in index order, then the
/// fewer-than-two-samples result. Each sample is read once into a local, checked and used from that local.
/// </summary>
public static class PowerIntegrator
{
    /// <summary>
    /// Integrates the samples over the window, in joules.
    /// </summary>
    /// <param name="samples">The samples in strictly increasing timestamp order, all inside the window.</param>
    /// <param name="windowStartSeconds">The window start in seconds since meter start.</param>
    /// <param name="windowStopSeconds">The window stop in seconds since meter start.</param>
    /// <returns>The energy in joules, or <c>null</c> when there are fewer than two samples.</returns>
    /// <exception cref="ArgumentException">
    /// The window is not finite, empty or inverted, or its duration is not finite; a timestamp or watts value is not finite;
    /// watts is negative; a sample lies outside the window; or timestamps are not strictly increasing.
    /// A sample message names the offending index. All of these are checked before the fewer-than-two-samples result.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The integral overflows to a value that is not finite.</exception>
    public static double? IntegrateJoules(ReadOnlySpan<PowerSample> samples, double windowStartSeconds, double windowStopSeconds)
    {
        ValidateWindow(windowStartSeconds, windowStopSeconds);

        // The accumulator starts at +0.0 so that a sum of -0.0 terms is +0.0.
        double joules = 0.0;
        double previousSeconds = double.NegativeInfinity;
        double previousWatts = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            PowerSample current = samples[i];
            CheckSample(i, current, previousSeconds, windowStartSeconds, windowStopSeconds, nameof(samples));
            joules += i == 0
                ? current.Watts * (current.Seconds - windowStartSeconds)
                : 0.5 * (previousWatts + current.Watts) * (current.Seconds - previousSeconds);
            previousSeconds = current.Seconds;
            previousWatts = current.Watts;
        }

        if (samples.Length < 2)
        {
            return null;
        }

        joules += previousWatts * (windowStopSeconds - previousSeconds);
        return double.IsFinite(joules)
            ? joules
            : throw new ArgumentOutOfRangeException(nameof(samples), "The integral of the samples over the window is not finite.");
    }

    /// <summary>
    /// Integrates the samples over the window, in joules, with the contract of the span overload.
    /// </summary>
    /// <remarks>
    /// An array or a <see cref="List{T}"/> is read in place, so it must not be mutated during the call: hold the owner's
    /// lock or pass a snapshot. Any other list is copied once. A list whose <c>Count</c> disagrees with its contents
    /// is outside the contract.
    /// </remarks>
    /// <param name="samples">The samples in strictly increasing timestamp order, all inside the window.</param>
    /// <param name="windowStartSeconds">The window start in seconds since meter start.</param>
    /// <param name="windowStopSeconds">The window stop in seconds since meter start.</param>
    /// <returns>The energy in joules, or <c>null</c> when there are fewer than two samples.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">As for the span overload.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The integral overflows to a value that is not finite.</exception>
    public static double? IntegrateJoules(IReadOnlyList<PowerSample> samples, double windowStartSeconds, double windowStopSeconds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return IntegrateJoules(AsSpan(samples), windowStartSeconds, windowStopSeconds);
    }

    /// <summary>
    /// The average power over the window: the integral divided by the window duration.
    /// </summary>
    /// <param name="samples">The samples in strictly increasing timestamp order, all inside the window.</param>
    /// <param name="windowStartSeconds">The window start in seconds since meter start.</param>
    /// <param name="windowStopSeconds">The window stop in seconds since meter start.</param>
    /// <returns>The average in watts, or <c>null</c> when the integral is <c>null</c>.</returns>
    /// <exception cref="ArgumentException">As for <see cref="IntegrateJoules(ReadOnlySpan{PowerSample}, double, double)"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The integral or the average is not finite.</exception>
    public static double? AverageWatts(ReadOnlySpan<PowerSample> samples, double windowStartSeconds, double windowStopSeconds)
    {
        double? joules = IntegrateJoules(samples, windowStartSeconds, windowStopSeconds);
        if (joules is null)
        {
            return null;
        }

        // Defensive: not known to be reachable. A time-weighted mean of finite watts cannot exceed the largest sample beyond
        // rounding, and adjacent near-maximum samples overflow the integral (which throws) before the division is reached.
        double average = joules.Value / (windowStopSeconds - windowStartSeconds);
        return double.IsFinite(average)
            ? average
            : throw new ArgumentOutOfRangeException(nameof(samples), "The average power over the window is not finite.");
    }

    /// <summary>
    /// The average power over the window, with the contract of the span overload.
    /// </summary>
    /// <remarks>
    /// An array or a <see cref="List{T}"/> is read in place, so it must not be mutated during the call: hold the owner's
    /// lock or pass a snapshot. Any other list is copied once. A list whose <c>Count</c> disagrees with its contents
    /// is outside the contract.
    /// </remarks>
    /// <param name="samples">The samples in strictly increasing timestamp order, all inside the window.</param>
    /// <param name="windowStartSeconds">The window start in seconds since meter start.</param>
    /// <param name="windowStopSeconds">The window stop in seconds since meter start.</param>
    /// <returns>The average in watts, or <c>null</c> when there are fewer than two samples.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">As for the span overload.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The integral or the average is not finite.</exception>
    public static double? AverageWatts(IReadOnlyList<PowerSample> samples, double windowStartSeconds, double windowStopSeconds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return AverageWatts(AsSpan(samples), windowStartSeconds, windowStopSeconds);
    }

    /// <summary>
    /// The largest watts value among the samples of a window, validated exactly like the integral.
    /// </summary>
    /// <param name="samples">The samples in strictly increasing timestamp order, all inside the window.</param>
    /// <param name="windowStartSeconds">The window start in seconds since meter start.</param>
    /// <param name="windowStopSeconds">The window stop in seconds since meter start.</param>
    /// <returns>The peak in watts, or <c>null</c> when there are fewer than two samples.</returns>
    /// <exception cref="ArgumentException">As for <see cref="IntegrateJoules(ReadOnlySpan{PowerSample}, double, double)"/>.</exception>
    public static double? PeakWatts(ReadOnlySpan<PowerSample> samples, double windowStartSeconds, double windowStopSeconds)
    {
        ValidateWindow(windowStartSeconds, windowStopSeconds);

        // Watts are validated as not negative, so starting at +0.0 is safe and an all -0.0 input returns +0.0 like the integral.
        double peak = 0.0;
        double previousSeconds = double.NegativeInfinity;
        for (int i = 0; i < samples.Length; i++)
        {
            PowerSample current = samples[i];
            CheckSample(i, current, previousSeconds, windowStartSeconds, windowStopSeconds, nameof(samples));
            peak = Math.Max(peak, current.Watts);
            previousSeconds = current.Seconds;
        }

        return samples.Length < 2 ? null : peak;
    }

    /// <summary>
    /// The largest watts value among the samples of a window, with the contract of the span overload.
    /// </summary>
    /// <remarks>
    /// An array or a <see cref="List{T}"/> is read in place, so it must not be mutated during the call: hold the owner's
    /// lock or pass a snapshot. Any other list is copied once. A list whose <c>Count</c> disagrees with its contents
    /// is outside the contract.
    /// </remarks>
    /// <param name="samples">The samples in strictly increasing timestamp order, all inside the window.</param>
    /// <param name="windowStartSeconds">The window start in seconds since meter start.</param>
    /// <param name="windowStopSeconds">The window stop in seconds since meter start.</param>
    /// <returns>The peak in watts, or <c>null</c> when there are fewer than two samples.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">As for the span overload.</exception>
    public static double? PeakWatts(IReadOnlyList<PowerSample> samples, double windowStartSeconds, double windowStopSeconds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return PeakWatts(AsSpan(samples), windowStartSeconds, windowStopSeconds);
    }

    // Arrays and List<T> are viewed in place; any other list is copied once.
    private static ReadOnlySpan<PowerSample> AsSpan(IReadOnlyList<PowerSample> samples) => samples switch
    {
        PowerSample[] array => array,
        List<PowerSample> list => CollectionsMarshal.AsSpan(list),
        _ => ToArray(samples),
    };

    private static PowerSample[] ToArray(IReadOnlyList<PowerSample> samples)
    {
        PowerSample[] copy = new PowerSample[samples.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = samples[i];
        }

        return copy;
    }

    private static void ValidateWindow(double windowStartSeconds, double windowStopSeconds)
    {
        if (!double.IsFinite(windowStartSeconds))
        {
            throw new ArgumentException("The window start must be finite.", nameof(windowStartSeconds));
        }

        if (!double.IsFinite(windowStopSeconds))
        {
            throw new ArgumentException("The window stop must be finite.", nameof(windowStopSeconds));
        }

        if (windowStopSeconds <= windowStartSeconds)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The window must not be empty or inverted: start {windowStartSeconds}, stop {windowStopSeconds}."),
                nameof(windowStopSeconds));
        }

        if (!double.IsFinite(windowStopSeconds - windowStartSeconds))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The window duration must be finite: start {windowStartSeconds}, stop {windowStopSeconds}."),
                nameof(windowStopSeconds));
        }
    }

    // Checks one sample that the caller has already copied into a local, in the order: timestamp, watts, sign, window, ordering.
    private static void CheckSample(int index, PowerSample sample, double previousSeconds, double windowStartSeconds, double windowStopSeconds, string paramName)
    {
        if (!double.IsFinite(sample.Seconds))
        {
            throw Reject(index, "has a non-finite timestamp", paramName);
        }

        if (!double.IsFinite(sample.Watts))
        {
            throw Reject(index, "has non-finite watts", paramName);
        }

        if (sample.Watts < 0.0)
        {
            throw Reject(index, string.Create(CultureInfo.InvariantCulture, $"has negative watts {sample.Watts}"), paramName);
        }

        if (sample.Seconds < windowStartSeconds || sample.Seconds > windowStopSeconds)
        {
            throw Reject(index, string.Create(CultureInfo.InvariantCulture, $"at {sample.Seconds} s lies outside the window {windowStartSeconds} to {windowStopSeconds} s"), paramName);
        }

        if (sample.Seconds <= previousSeconds)
        {
            throw Reject(index, string.Create(CultureInfo.InvariantCulture, $"at {sample.Seconds} s is not after the previous sample at {previousSeconds} s"), paramName);
        }
    }

    private static ArgumentException Reject(int index, string problem, string paramName) =>
        new(string.Create(CultureInfo.InvariantCulture, $"The sample at index {index} {problem}."), paramName);
}