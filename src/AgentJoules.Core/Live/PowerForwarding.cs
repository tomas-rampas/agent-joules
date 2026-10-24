using System.Globalization;

namespace AgentJoules.Core.Live;

/// <summary>
/// How a <see cref="Energy.SampledEnergyMeter"/> forwards the power samples it already took to the live view (REQ-032, design 3.10): at most one sample per
/// <paramref name="IntervalMs"/> milliseconds, and every stored sample when the interval is shorter than the sampling period. Forwarding never reads the GPU.
/// </summary>
/// <param name="Sink">The live sink the samples go to; not null.</param>
/// <param name="IntervalMs">The forwarding interval in milliseconds (<c>Dashboard:PowerIntervalMs</c>); at least 1.</param>
public sealed record PowerForwarding(ILiveEventSink Sink, int IntervalMs)
{
    /// <summary>The live sink the samples go to.</summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public ILiveEventSink Sink { get; init; } = Sink ?? throw new ArgumentNullException(nameof(Sink));

    /// <summary>The forwarding interval in milliseconds.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 1.</exception>
    public int IntervalMs
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(IntervalMs), "The forwarding interval must be at least 1 ms.");
    } = IntervalMs >= 1 ? IntervalMs : throw new ArgumentOutOfRangeException(nameof(IntervalMs), "The forwarding interval must be at least 1 ms.");

    /// <summary>
    /// The startup note for an interval shorter than the sampling period, which forwards every stored sample, or null when the interval is not shorter. The period is printed in whole
    /// milliseconds, invariant culture.
    /// </summary>
    /// <param name="intervalMs">The configured <c>Dashboard:PowerIntervalMs</c>.</param>
    /// <param name="sampleRateHz">The sampling rate of the meter (<c>Meter:SampleRateHz</c>); positive and finite.</param>
    /// <returns>The note, or null.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleRateHz"/> is not positive and finite.</exception>
    public static string? ShortIntervalNote(int intervalMs, double sampleRateHz)
    {
        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "The sampling rate must be positive and finite.");
        }

        double periodMs = 1000.0 / sampleRateHz;
        if (intervalMs >= periodMs)
        {
            return null;
        }

        string period = Math.Round(periodMs, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        string interval = intervalMs.ToString(CultureInfo.InvariantCulture);
        return $"Dashboard:PowerIntervalMs {interval} is shorter than the sampling period of {period} ms; every power sample is forwarded.";
    }
}