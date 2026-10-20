using System.Collections.ObjectModel;
using System.Globalization;

using AgentJoules.Core.Labels;

using GpuThrottleReasons = AgentJoules.Core.Gpu.ThrottleReasons;

namespace AgentJoules.Core.Energy;

/// <summary>
/// The shape rules of an energy reading's metrics, flags and throttle reasons, shared by <see cref="EnergyReading"/> and the step reading of the
/// results model, so that a document read back is held to the same invariants as a reading built in memory. Every rule throws an
/// <see cref="ArgumentException"/> that names the offending constructor parameter.
/// </summary>
internal static class EnergyShapeRules
{
    /// <summary>The unit of an energy metric.</summary>
    internal const string JouleUnit = "J";

    /// <summary>The unit of a power metric.</summary>
    internal const string WattUnit = "W";

    // The flags that describe a measurement or a baseline; a hosted tier has neither (it skips the baseline), so none of them can be set on its
    // reading. SharedWindow stays allowed: the run assembler applies it to any step.
    private const ReadingFlags EnergyFlags =
        ReadingFlags.SamplingFallback | ReadingFlags.LowResolution | ReadingFlags.Throttled | ReadingFlags.BelowBaseline | ReadingFlags.BaselineUnstable;

    // A reading holds measured quantities or an explicit absence; an estimated or model-judged metric never belongs in it.
    internal static void RequireShape(string parameter, Metric metric, string unit)
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

    // Net is published negative together with its flag, never one without the other (REQ-005).
    internal static void RequireNetFlagPairing(Metric netJoules, ReadingFlags flags)
    {
        bool netIsNegative = netJoules is { Label: MetricLabel.Measured, Value: < 0.0 };
        if (netIsNegative != flags.HasFlag(ReadingFlags.BelowBaseline))
        {
            throw netIsNegative
                ? Rejected("flags", $"A measured negative net requires the {nameof(ReadingFlags.BelowBaseline)} flag.")
                : Rejected("flags", $"The {nameof(ReadingFlags.BelowBaseline)} flag requires a measured negative net.");
        }
    }

    internal static void RequireFallbackMethod(ReadingFlags flags, EnergyMethod? method)
    {
        if (flags.HasFlag(ReadingFlags.SamplingFallback) && method != EnergyMethod.Sampling)
        {
            throw Rejected("flags", $"The {nameof(ReadingFlags.SamplingFallback)} flag requires the sampling method, but the method is {(method is null ? "absent" : method.ToString())}.");
        }
    }

    // A hosted tier measures nothing: every energy metric says so with the one reason, and no flag describes a measurement.
    internal static void RequireHostedNotApplicable(string parameter, Metric metric)
    {
        if (metric.Label != MetricLabel.NotApplicable || !string.Equals(metric.Reason, MeterCapabilities.HostedTierReason, StringComparison.Ordinal))
        {
            throw Rejected(parameter, $"A hosted-tier metric must be N/A with the reason {MeterCapabilities.HostedTierReason}, but it is {metric.Label}.");
        }
    }

    internal static void RequireNoEnergyFlags(ReadingFlags flags)
    {
        if ((flags & EnergyFlags) != ReadingFlags.None)
        {
            throw Rejected("flags", $"A hosted tier measures no energy, so the flags must not name a measurement, but they are {string.Join(", ", ReadingFlagNames.ToStrings(flags))}.");
        }
    }

    internal static ReadOnlyCollection<string>? CopyReasons(IReadOnlyList<string>? reasons)
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
    internal static void RequireThrottleShape(ReadingFlags flags, ReadOnlyCollection<string>? reasons)
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

    internal static ArgumentException Rejected(string parameterName, FormattableString message) =>
        new(message.ToString(CultureInfo.InvariantCulture), parameterName);

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
}