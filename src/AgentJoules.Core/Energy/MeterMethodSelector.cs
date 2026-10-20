using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using AgentJoules.Core.Cli;
using AgentJoules.Core.Gpu;
using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Energy;

/// <summary>
/// Chooses between the energy counter and power sampling from <c>Meter:Method</c> and the state of the opened GPU.
/// </summary>
/// <remarks>
/// The single counter read of REQ-004 is the probe the GPU source factory makes at open; the selector reads
/// <see cref="IGpuTelemetrySource.EnergyCounterSupported"/> and never asks the GPU again. Sampling that was forced with
/// <c>sampling</c> is not a fallback (design 3.2): only <c>auto</c> on a GPU without the counter sets
/// <see cref="MeterCapabilities.IsFallback"/>. The overloads that take the counter state as values serve callers that have
/// no GPU source, such as a deterministic meter; the overloads over a source delegate to them.
/// </remarks>
public static class MeterMethodSelector
{
    private const string UnknownReason = "counter-not-supported";

    // The valid names are at most 8 characters, so a longer value is never a typo of one; it may be a key in the wrong setting.
    private const int MaxEchoedLength = 16;

    /// <summary>Applies <paramref name="configuredMethod"/> to the state of <paramref name="source"/>.</summary>
    /// <param name="configuredMethod"><c>auto</c>, <c>counter</c> or <c>sampling</c>, trimmed and matched without regard to case.</param>
    /// <param name="source">The opened GPU whose counter state the factory decided at open.</param>
    /// <param name="sampleRateHz">The configured sampling rate; positive and finite.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ConfigurationException">The method is not one of the three names, or the rate is not positive and finite.</exception>
    /// <exception cref="MeasurementPreconditionException">The method is <c>counter</c> and the GPU has no counter.</exception>
    public static MeterCapabilities Select(string? configuredMethod, IGpuTelemetrySource source, double sampleRateHz)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Select(configuredMethod, source.EnergyCounterSupported, source.EnergyCounterUnsupportedReason, sampleRateHz);
    }

    /// <summary>Applies <paramref name="configuredMethod"/> to a counter state given as values.</summary>
    /// <param name="configuredMethod"><c>auto</c>, <c>counter</c> or <c>sampling</c>, trimmed and matched without regard to case.</param>
    /// <param name="counterSupported">True when the counter answered the startup probe.</param>
    /// <param name="counterUnsupportedReason">Why it did not; ignored when <paramref name="counterSupported"/> is true, and a missing or blank reason becomes <c>counter-not-supported</c>.</param>
    /// <param name="sampleRateHz">The configured sampling rate; positive and finite.</param>
    /// <exception cref="ConfigurationException">The method is not one of the three names, or the rate is not positive and finite.</exception>
    /// <exception cref="MeasurementPreconditionException">The method is <c>counter</c> and the counter is unsupported.</exception>
    public static MeterCapabilities Select(string? configuredMethod, bool counterSupported, string? counterUnsupportedReason, double sampleRateHz)
    {
        MeterMethodSetting setting = Parse(configuredMethod);
        return TrySelect(setting, counterSupported, counterUnsupportedReason, sampleRateHz, out MeterCapabilities? capabilities)
            ? capabilities
            : throw new MeasurementPreconditionException(
                $"Meter:Method=counter needs the GPU energy counter, but it is unsupported ({UnsupportedReason(counterUnsupportedReason)}). Use Meter:Method=auto or Meter:Method=sampling.");
    }

    /// <summary>Parses <c>Meter:Method</c>: trimmed and matched without regard to case.</summary>
    /// <exception cref="ConfigurationException">
    /// The value is not <c>auto</c>, <c>counter</c> or <c>sampling</c>. The message echoes a value of at most 16 characters
    /// with control characters replaced, and only the length of a longer one.
    /// </exception>
    public static MeterMethodSetting Parse(string? configuredMethod)
    {
        string method = configuredMethod?.Trim() ?? string.Empty;
        MeterMethodSetting? parsed =
            method.Equals("auto", StringComparison.OrdinalIgnoreCase) ? MeterMethodSetting.Auto
            : method.Equals("counter", StringComparison.OrdinalIgnoreCase) ? MeterMethodSetting.Counter
            : method.Equals("sampling", StringComparison.OrdinalIgnoreCase) ? MeterMethodSetting.Sampling
            : null;

        return parsed ?? throw new ConfigurationException($"Meter:Method must be auto, counter or sampling; got {Echo(configuredMethod, method)}.");
    }

    /// <summary>Applies a parsed setting to the state of <paramref name="source"/>.</summary>
    /// <param name="setting">The parsed <c>Meter:Method</c>.</param>
    /// <param name="source">The opened GPU whose counter state the factory decided at open.</param>
    /// <param name="sampleRateHz">The configured sampling rate; positive and finite.</param>
    /// <param name="capabilities">The decision when the result is true; null when the counter is forced but unsupported.</param>
    /// <returns>False only when <paramref name="setting"/> is <see cref="MeterMethodSetting.Counter"/> and the counter is unsupported.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="setting"/> is not a defined value.</exception>
    /// <exception cref="ConfigurationException">The rate is not positive and finite.</exception>
    public static bool TrySelect(
        MeterMethodSetting setting,
        IGpuTelemetrySource source,
        double sampleRateHz,
        [NotNullWhen(true)] out MeterCapabilities? capabilities)
    {
        ArgumentNullException.ThrowIfNull(source);
        return TrySelect(setting, source.EnergyCounterSupported, source.EnergyCounterUnsupportedReason, sampleRateHz, out capabilities);
    }

    /// <summary>Applies a parsed setting to a counter state given as values.</summary>
    /// <param name="setting">The parsed <c>Meter:Method</c>.</param>
    /// <param name="counterSupported">True when the counter answered the startup probe.</param>
    /// <param name="counterUnsupportedReason">Why it did not; ignored when <paramref name="counterSupported"/> is true, and a missing or blank reason becomes <c>counter-not-supported</c>.</param>
    /// <param name="sampleRateHz">The configured sampling rate; positive and finite.</param>
    /// <param name="capabilities">The decision when the result is true; null when the counter is forced but unsupported.</param>
    /// <returns>False only when <paramref name="setting"/> is <see cref="MeterMethodSetting.Counter"/> and the counter is unsupported.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="setting"/> is not a defined value.</exception>
    /// <exception cref="ConfigurationException">The rate is not positive and finite; it is checked before the counter state.</exception>
    public static bool TrySelect(
        MeterMethodSetting setting,
        bool counterSupported,
        string? counterUnsupportedReason,
        double sampleRateHz,
        [NotNullWhen(true)] out MeterCapabilities? capabilities)
    {
        if (!Enum.IsDefined(setting))
        {
            throw new ArgumentOutOfRangeException(nameof(setting));
        }

        if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0.0)
        {
            throw new ConfigurationException(string.Create(
                CultureInfo.InvariantCulture,
                $"Meter:SampleRateHz must be a positive finite number; got {sampleRateHz}."));
        }

        capabilities = null;
        if (setting == MeterMethodSetting.Counter && !counterSupported)
        {
            return false;
        }

        // Counter needs the counter, auto prefers it, and only auto without it is a fallback; forced sampling never is.
        EnergyMethod chosen = counterSupported && setting != MeterMethodSetting.Sampling ? EnergyMethod.Counter : EnergyMethod.Sampling;
        string? reason = counterSupported ? null : UnsupportedReason(counterUnsupportedReason);
        capabilities = new MeterCapabilities(chosen, counterSupported, reason, sampleRateHz, isFallback: setting == MeterMethodSetting.Auto && !counterSupported);
        return true;
    }

    // The reason comes from the source and reaches messages and the results file, so it is echoed through the same filter.
    // A reason that is missing, blank or made only of characters the filter replaced (zero-width space, NEL) says nothing.
    private static string UnsupportedReason(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return UnknownReason;
        }

        string echoed = TextEcho.ForMessage(raw);
        return string.IsNullOrWhiteSpace(echoed.Replace("?", string.Empty, StringComparison.Ordinal)) ? UnknownReason : echoed;
    }

    private static string Echo(string? configured, string trimmed) =>
        configured is null ? "(null)"
        : trimmed.Length <= MaxEchoedLength ? $"'{TextEcho.ForMessage(trimmed)}'"
        : string.Create(CultureInfo.InvariantCulture, $"a value of {trimmed.Length} characters");
}