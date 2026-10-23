using AgentJoules.Core.Results;

namespace AgentJoules.Core.Energy;

/// <summary>What the meter measures with and why: the chosen method, the counter state and whether sampling is a fallback.</summary>
/// <remarks>
/// The constructor checks the invariants, and every property is get-only, so an instance cannot describe a combination
/// that is impossible: a fallback is sampling on a GPU without the counter, the counter method needs the counter, and the
/// unsupported reason is present exactly when the counter is unsupported. The unmetered method (hosted tiers) is the one
/// exception to the positive sampling rate: it has no sampling, so it accepts only counter unsupported with the reason
/// <see cref="HostedTierReason"/>, rate 0 and no fallback.
/// </remarks>
public sealed record MeterCapabilities
{
    /// <summary>The reason a hosted tier carries as its counter-unsupported reason and on every N/A energy metric.</summary>
    public const string HostedTierReason = "hosted-tier";

    /// <summary>Creates the capabilities after checking that the combination is consistent.</summary>
    /// <param name="method">The method that supplies the gross energy of every window; a defined value.</param>
    /// <param name="counterSupported">True when the GPU's energy counter answered the startup probe.</param>
    /// <param name="counterUnsupportedReason">Why the counter is unsupported, for example <c>counter-not-supported: NVML_ERROR_NOT_SUPPORTED</c>; null exactly when it is supported; <see cref="HostedTierReason"/> for the unmetered method.</param>
    /// <param name="sampleRateHz">The configured sampling rate in hertz; positive and finite, except that the unmetered method has no sampling and needs exactly 0.</param>
    /// <param name="isFallback">True only when automatic selection fell back to sampling because the counter is unsupported; every reading then carries the <c>sampling-fallback</c> flag.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="method"/> is not defined, or the rate is not positive and finite.</exception>
    /// <exception cref="ArgumentException">The combination of the other arguments is inconsistent.</exception>
    public MeterCapabilities(EnergyMethod method, bool counterSupported, string? counterUnsupportedReason, double sampleRateHz, bool isFallback)
    {
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method));
        }

        if (method == EnergyMethod.Unmetered)
        {
            RequireHostedTier(counterSupported, counterUnsupportedReason, sampleRateHz, isFallback);
        }
        else if (!double.IsFinite(sampleRateHz) || sampleRateHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "The sample rate must be positive and finite.");
        }

        if (counterSupported ? counterUnsupportedReason is not null : string.IsNullOrWhiteSpace(counterUnsupportedReason))
        {
            throw new ArgumentException("The unsupported reason must be null when the counter is supported and not blank when it is not.", nameof(counterUnsupportedReason));
        }

        if (method == EnergyMethod.Counter && !counterSupported)
        {
            throw new ArgumentException("The counter method needs a supported counter.", nameof(method));
        }

        if (isFallback && (method != EnergyMethod.Sampling || counterSupported))
        {
            throw new ArgumentException("A fallback is sampling on a GPU whose counter is unsupported.", nameof(isFallback));
        }

        Method = method;
        CounterSupported = counterSupported;
        CounterUnsupportedReason = counterUnsupportedReason;
        SampleRateHz = sampleRateHz;
        IsFallback = isFallback;
    }

    /// <summary>The method that supplies the gross energy of every window.</summary>
    public EnergyMethod Method { get; }

    /// <summary>True when the GPU's energy counter answered the startup probe.</summary>
    public bool CounterSupported { get; }

    /// <summary>Why the counter is unsupported; null when it is supported.</summary>
    public string? CounterUnsupportedReason { get; }

    /// <summary>The configured sampling rate in hertz.</summary>
    public double SampleRateHz { get; }

    /// <summary>True only when automatic selection fell back to sampling because the counter is unsupported.</summary>
    public bool IsFallback { get; }

    /// <summary>The startup console disclosure of the method behind every joule value.</summary>
    public string Describe() => (Method, IsFallback) switch
    {
        (EnergyMethod.Sampling, false) => "Energy method: sampling",
        (EnergyMethod.Sampling, true) => ReportSentences.SamplingFallback,
        (EnergyMethod.Counter, false) => "Energy method: counter",
        (EnergyMethod.Unmetered, false) => "Energy method: unmetered (hosted tier)",
        _ => throw new InvalidOperationException("The capabilities are inconsistent."),
    };

    // The one combination that describes a hosted tier: no counter (the GPU is not ours), the hosted-tier reason, no sampling rate and no fallback.
    private static void RequireHostedTier(bool counterSupported, string? counterUnsupportedReason, double sampleRateHz, bool isFallback)
    {
        if (sampleRateHz != 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz), "The unmetered method has no sampling, so its sample rate must be 0.");
        }

        if (counterSupported || !string.Equals(counterUnsupportedReason, HostedTierReason, StringComparison.Ordinal))
        {
            throw new ArgumentException($"The unmetered method needs an unsupported counter with the reason {HostedTierReason}.", nameof(counterUnsupportedReason));
        }

        if (isFallback)
        {
            throw new ArgumentException("The unmetered method is not a sampling fallback.", nameof(isFallback));
        }
    }
}