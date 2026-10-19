using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using AgentJoules.Core.Labels;

using GpuThrottleReasons = AgentJoules.Core.Gpu.ThrottleReasons;

namespace AgentJoules.Core.Energy;

/// <summary>
/// The single reading shape both meters produce for one window (design 3.2). In production <see cref="ReadingCalculator"/>
/// builds it; tests and deserialisation build it through the same public constructor, which checks every invariant, so a
/// reading that exists is always well formed. A value that could not be determined is a not-reported <see cref="Metric"/>
/// with a reason, never 0. Deserialising a document runs the constructor checks: a violated invariant (a missing or null metric,
/// a non-positive duration, a negative count, a non-positive rate, flags outside the vocabulary, a label other than measured,
/// not-reported or N/A, a measured value without its unit J or W, a measured negative net without the below-baseline flag or that flag
/// without such a net, the sampling-fallback flag with the counter method, the throttled flag without throttle reasons or reasons
/// without the flag, a reason that is not one of the five throttle names or is repeated or out of bit order) surfaces as an unwrapped
/// <see cref="ArgumentException"/>, and a malformed token (an unknown method, a bad flag name) as a <see cref="JsonException"/>.
/// </summary>
public sealed record EnergyReading
{
    private const string JouleUnit = "J";
    private const string WattUnit = "W";

    /// <summary>Creates a reading after checking every invariant.</summary>
    /// <param name="grossJoules">The energy the GPU drew in the window, in joules.</param>
    /// <param name="netJoules">The gross energy minus the idle baseline power times the duration; negative values are kept.</param>
    /// <param name="averageWatts">The sampling integral divided by the window duration, in watts.</param>
    /// <param name="peakWatts">The largest power sample in the window, in watts.</param>
    /// <param name="durationSeconds">The window length in seconds; positive and finite.</param>
    /// <param name="method">The method that produced <paramref name="grossJoules"/>; a defined member.</param>
    /// <param name="sampleCount">The number of power samples in the window, both edges included; not negative.</param>
    /// <param name="sampleRateHz">The configured sampling rate in hertz; positive and finite.</param>
    /// <param name="flags">The qualifiers of this reading; only bits of the vocabulary.</param>
    /// <param name="throttleReasons">
    /// The names of the throttle reasons seen in the window: listed when any sample carried a throttling bit, an empty list when every
    /// sample was supported and none was active, <c>null</c> when the driver could not report them for some sample and none was seen.
    /// Every entry is one of <c>SwPowerCap</c>, <c>HwSlowdown</c>, <c>SwThermalSlowdown</c>, <c>HwThermalSlowdown</c>
    /// and <c>HwPowerBrakeSlowdown</c>, in that (bit) order and without duplicates; a non-empty list and the throttled flag imply each other.
    /// The list is copied.
    /// </param>
    /// <exception cref="ArgumentException">A metric is null, or another argument violates the rule named above.</exception>
    [JsonConstructor]
    public EnergyReading(
        Metric grossJoules,
        Metric netJoules,
        Metric averageWatts,
        Metric peakWatts,
        double durationSeconds,
        EnergyMethod method,
        int sampleCount,
        double sampleRateHz,
        ReadingFlags flags,
        IReadOnlyList<string>? throttleReasons)
    {
        ArgumentNullException.ThrowIfNull(grossJoules);
        ArgumentNullException.ThrowIfNull(netJoules);
        ArgumentNullException.ThrowIfNull(averageWatts);
        ArgumentNullException.ThrowIfNull(peakWatts);

        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0.0)
        {
            throw Rejected(nameof(durationSeconds), $"The duration must be positive and finite, but it is {durationSeconds} s.");
        }

        if (!Enum.IsDefined(method))
        {
            throw Rejected(nameof(method), $"The method {(int)method} is not a defined energy method.");
        }

        if (sampleCount < 0)
        {
            throw Rejected(nameof(sampleCount), $"The sample count must not be negative, but it is {sampleCount}.");
        }

        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0.0)
        {
            throw Rejected(nameof(sampleRateHz), $"The sample rate must be positive and finite, but it is {sampleRateHz} Hz.");
        }

        if (!ReadingFlagNames.IsKnown(flags))
        {
            throw Rejected(nameof(flags), $"The flags value {(int)flags} has a bit outside the reading-flag vocabulary.");
        }

        RequireShape(nameof(grossJoules), grossJoules, JouleUnit);
        RequireShape(nameof(netJoules), netJoules, JouleUnit);
        RequireShape(nameof(averageWatts), averageWatts, WattUnit);
        RequireShape(nameof(peakWatts), peakWatts, WattUnit);

        // Net is published negative together with its flag, never one without the other (REQ-005).
        bool netIsNegative = netJoules is { Label: MetricLabel.Measured, Value: < 0.0 };
        if (netIsNegative != flags.HasFlag(ReadingFlags.BelowBaseline))
        {
            throw netIsNegative
                ? Rejected(nameof(flags), $"A measured negative net requires the {nameof(ReadingFlags.BelowBaseline)} flag.")
                : Rejected(nameof(flags), $"The {nameof(ReadingFlags.BelowBaseline)} flag requires a measured negative net.");
        }

        if (flags.HasFlag(ReadingFlags.SamplingFallback) && method != EnergyMethod.Sampling)
        {
            throw Rejected(nameof(flags), $"The {nameof(ReadingFlags.SamplingFallback)} flag requires the sampling method, but the method is {method}.");
        }

        ReadOnlyCollection<string>? reasons = CopyReasons(throttleReasons);
        RequireThrottleShape(flags, reasons);

        GrossJoules = grossJoules;
        NetJoules = netJoules;
        AverageWatts = averageWatts;
        PeakWatts = peakWatts;
        DurationSeconds = durationSeconds;
        Method = method;
        SampleCount = sampleCount;
        SampleRateHz = sampleRateHz;
        Flags = flags;
        ThrottleReasons = reasons;
    }

    /// <summary>The energy the GPU drew in the window, in joules.</summary>
    public Metric GrossJoules { get; }

    /// <summary>The gross energy minus the idle baseline power times the duration; negative values are kept and flagged.</summary>
    public Metric NetJoules { get; }

    /// <summary>The sampling integral divided by the window duration, in watts; serialized as <c>avgW</c>.</summary>
    [JsonPropertyName("avgW")]
    public Metric AverageWatts { get; }

    /// <summary>The largest power sample in the window, in watts; serialized as <c>peakW</c>.</summary>
    [JsonPropertyName("peakW")]
    public Metric PeakWatts { get; }

    /// <summary>The window length in seconds.</summary>
    public double DurationSeconds { get; }

    /// <summary>The method that produced <see cref="GrossJoules"/>; always the strict string form, whatever the caller's options say.</summary>
    [JsonConverter(typeof(EnergyMethodJsonConverter))]
    public EnergyMethod Method { get; }

    /// <summary>The number of power samples in the window, both edges included.</summary>
    public int SampleCount { get; }

    /// <summary>The configured sampling rate in hertz.</summary>
    public double SampleRateHz { get; }

    /// <summary>
    /// The qualifiers of this reading, serialized as an array of vocabulary strings. A property-level converter outranks a
    /// <see cref="JsonStringEnumConverter"/> in the caller's options, which would otherwise win over the attribute on the enum.
    /// </summary>
    [JsonConverter(typeof(ReadingFlagsJsonConverter))]
    public ReadingFlags Flags { get; }

    /// <summary>
    /// The names of the throttle reasons seen in the window. The names are listed when any sample carried a throttling bit (a positive
    /// claim needs one sample); an empty list means every sample of the window was supported and none was active; <c>null</c> means
    /// the driver could not report them for at least one sample and no throttling bit was seen, so nothing can be said. Every entry is
    /// one of <c>SwPowerCap</c>, <c>HwSlowdown</c>, <c>SwThermalSlowdown</c>, <c>HwThermalSlowdown</c> and <c>HwPowerBrakeSlowdown</c>,
    /// in that (bit) order and without duplicates; a non-empty list and the throttled flag imply each other.
    /// </summary>
    public IReadOnlyList<string>? ThrottleReasons { get; }

    // A reading holds measured quantities or an explicit absence; an estimated or model-judged metric never belongs in it.
    private static void RequireShape(string parameter, Metric metric, string unit)
    {
        if (metric.Label is not (MetricLabel.Measured or MetricLabel.NotReported or MetricLabel.NotApplicable))
        {
            throw Rejected(parameter, $"The metric must be measured, not-reported or N/A, but it is {metric.Label}.");
        }

        if (metric.Label == MetricLabel.Measured && !string.Equals(metric.Unit, unit, StringComparison.Ordinal))
        {
            throw Rejected(parameter, $"A measured metric must carry the unit {unit}, but it carries {(metric.Unit is null ? "none" : TextEcho.ForMessage(metric.Unit))}.");
        }
    }

    private static ReadOnlyCollection<string>? CopyReasons(IReadOnlyList<string>? reasons)
    {
        if (reasons is null)
        {
            return null;
        }

        string[] copy = new string[reasons.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            string? reason = reasons[i];
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw Rejected("throttleReasons", $"The throttle reason at index {i} must not be null or blank.");
            }

            copy[i] = reason;
        }

        return Array.AsReadOnly(copy);
    }

    // The throttled flag and a non-empty list imply each other (EDGE-013); the names are the five throttle names in bit order, each once.
    private static void RequireThrottleShape(ReadingFlags flags, ReadOnlyCollection<string>? reasons)
    {
        IReadOnlyList<string> vocabulary = GpuThrottleReasons.AllNames;
        int previous = -1;
        if (reasons is not null)
        {
            for (int i = 0; i < reasons.Count; i++)
            {
                int index = IndexOf(vocabulary, reasons[i]);
                if (index < 0)
                {
                    throw Rejected("throttleReasons", $"The throttle reason at index {i} must be one of {string.Join(", ", vocabulary)}, but it is {TextEcho.ForMessage(reasons[i])}.");
                }

                if (index <= previous)
                {
                    throw Rejected("throttleReasons", $"The throttle reason {reasons[i]} at index {i} is repeated or out of bit order ({string.Join(", ", vocabulary)}).");
                }

                previous = index;
            }
        }

        bool hasReasons = reasons is { Count: > 0 };
        if (hasReasons != flags.HasFlag(ReadingFlags.Throttled))
        {
            throw hasReasons
                ? Rejected("flags", $"Throttle reasons require the {nameof(ReadingFlags.Throttled)} flag.")
                : Rejected("flags", $"The {nameof(ReadingFlags.Throttled)} flag requires at least one throttle reason.");
        }
    }

    private static int IndexOf(IReadOnlyList<string> vocabulary, string name)
    {
        for (int i = 0; i < vocabulary.Count; i++)
        {
            if (string.Equals(vocabulary[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static ArgumentException Rejected(string parameterName, FormattableString message) =>
        new(message.ToString(CultureInfo.InvariantCulture), parameterName);
}