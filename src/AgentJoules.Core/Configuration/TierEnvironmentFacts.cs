namespace AgentJoules.Core.Configuration;

/// <summary>
/// The facts of a tier that the run document reports in its <c>environment</c> object and that come from the configuration, not from a
/// measurement (REQ-012): today the reasoning mode of the tier. The run assembler reads them from here, so the word in the results is the word
/// of <c>Tiers:&lt;tier&gt;:ReasoningMode</c> and nothing else.
/// </summary>
/// <remarks>A plain immutable class: a scan test keeps the newer type shapes (see ConfigValidationTests) out of the folder of the options.</remarks>
public sealed class TierEnvironmentFacts
{
    private TierEnvironmentFacts(string reasoningMode)
    {
        ReasoningMode = reasoningMode;
    }

    /// <summary>The value of <c>Tiers:&lt;tier&gt;:ReasoningMode</c>: <c>off</c>, <c>on</c> or <c>unsupported</c> (one of <see cref="TierOptions.ReasoningModes"/>).</summary>
    public string ReasoningMode { get; }

    /// <summary>Reads the facts of <paramref name="tier"/>.</summary>
    /// <param name="tier">The options of the tier.</param>
    /// <returns>The facts.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tier"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="tier"/> holds a reasoning mode that is not one of <see cref="TierOptions.ReasoningModes"/> (the configuration validator rejects it earlier, so this guards a caller that skipped the validation).</exception>
    public static TierEnvironmentFacts From(TierOptions tier)
    {
        ArgumentNullException.ThrowIfNull(tier);

        return TierOptions.ReasoningModes.Contains(tier.ReasoningMode, StringComparer.Ordinal)
            ? new TierEnvironmentFacts(tier.ReasoningMode)
            : throw new ArgumentException("The tier's ReasoningMode is not off, on or unsupported.", nameof(tier));
    }
}