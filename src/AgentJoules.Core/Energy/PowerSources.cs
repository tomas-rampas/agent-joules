namespace AgentJoules.Core.Energy;

/// <summary>
/// The values of <c>environment.powerSource</c> (REQ-021, AJ-106): where the machine that ran the client took its power from during the run. A laptop on its battery draws a different GPU power
/// than the same laptop on mains, so a result states which one it was; a machine that cannot say states <see cref="Unknown"/>, never a guess. The strings are the exact values of the file.
/// </summary>
public static class PowerSources
{
    /// <summary>The machine runs on mains power (an AC adapter is connected).</summary>
    public const string Ac = "ac";

    /// <summary>The machine runs on its battery.</summary>
    public const string Battery = "battery";

    /// <summary>The power source could not be determined (no supply files, an operating system without a reader, a read that failed, or a harness that never reads the host).</summary>
    public const string Unknown = "unknown";

    /// <summary>Tells whether a string is one of the three values; the comparison is ordinal, so the case matters.</summary>
    /// <param name="value">The string to look up, or null.</param>
    /// <returns>True when <paramref name="value"/> is <see cref="Ac"/>, <see cref="Battery"/> or <see cref="Unknown"/>.</returns>
    public static bool IsKnown(string? value) =>
        string.Equals(value, Ac, StringComparison.Ordinal) || string.Equals(value, Battery, StringComparison.Ordinal) || string.Equals(value, Unknown, StringComparison.Ordinal);

    /// <summary>Keeps a detected value that is one of the three and turns anything else (null, blank, another text) into <see cref="Unknown"/>, so a file never holds a value outside the vocabulary.</summary>
    /// <param name="value">The value a detector returned, or null.</param>
    /// <returns>The same value when it is known; otherwise <see cref="Unknown"/>.</returns>
    public static string Normalize(string? value) => IsKnown(value) ? value! : Unknown;
}