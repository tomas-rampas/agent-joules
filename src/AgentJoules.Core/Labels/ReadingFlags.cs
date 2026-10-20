using System.Text.Json.Serialization;

namespace AgentJoules.Core.Labels;

/// <summary>
/// Qualifiers attached to one energy reading; each bit has a fixed vocabulary string in <see cref="ReadingFlagNames"/>.
/// Serialized as an array of those strings by <see cref="ReadingFlagsJsonConverter"/>.
/// </summary>
[Flags]
[JsonConverter(typeof(ReadingFlagsJsonConverter))]
public enum ReadingFlags
{
    /// <summary>No qualifier.</summary>
    None = 0,

    /// <summary>The energy counter was unavailable and the sampling integral was used.</summary>
    SamplingFallback = 1,

    /// <summary>The window was too short for the sampling rate.</summary>
    LowResolution = 2,

    /// <summary>The window was shared by overlapping steps.</summary>
    SharedWindow = 4,

    /// <summary>The idle baseline varied too much between samples.</summary>
    BaselineUnstable = 8,

    /// <summary>The gross energy was below the baseline, so the net value is negative.</summary>
    BelowBaseline = 16,

    /// <summary>The GPU reported a throttle reason during the window.</summary>
    Throttled = 32,

    /// <summary>The model was only partly offloaded to the GPU.</summary>
    PartialOffload = 64,

    /// <summary>The step produced more output tokens than the output limit of its task (a step-level qualifier; an energy reading never carries it).</summary>
    MaxTokensExceeded = 128,
}

/// <summary>Maps <see cref="ReadingFlags"/> to the vocabulary strings of the results file and back.</summary>
public static class ReadingFlagNames
{
    // One ordered table (bit order) is the source for ToStrings, Parse and TryParse.
    private static readonly (ReadingFlags Flag, string Name)[] Table =
    [
        (ReadingFlags.SamplingFallback, "sampling-fallback"),
        (ReadingFlags.LowResolution, "low-resolution"),
        (ReadingFlags.SharedWindow, "shared-window"),
        (ReadingFlags.BaselineUnstable, "baseline-unstable"),
        (ReadingFlags.BelowBaseline, "below-baseline"),
        (ReadingFlags.Throttled, "throttled"),
        (ReadingFlags.PartialOffload, "partial-offload"),
        (ReadingFlags.MaxTokensExceeded, "max-tokens-exceeded"),
    ];

    // Every bit the table names; a bit outside it is a caller error, not something to drop silently.
    private static readonly ReadingFlags KnownMask = Table.Aggregate(ReadingFlags.None, static (mask, entry) => mask | entry.Flag);

    /// <summary>True when every set bit has a vocabulary string; <see cref="ReadingFlags.None"/> is known.</summary>
    /// <param name="flags">The flags to check.</param>
    internal static bool IsKnown(ReadingFlags flags) => (flags & ~KnownMask) == ReadingFlags.None;

    /// <summary>Returns the vocabulary string of every set bit in bit order; an empty list for <see cref="ReadingFlags.None"/>.</summary>
    /// <param name="flags">The flags to convert.</param>
    /// <exception cref="ArgumentOutOfRangeException">A bit is set that has no vocabulary string.</exception>
    public static IReadOnlyList<string> ToStrings(ReadingFlags flags)
    {
        if ((flags & ~KnownMask) != ReadingFlags.None)
        {
            throw new ArgumentOutOfRangeException(nameof(flags), (int)flags, "The flags value has a bit outside the reading-flag vocabulary.");
        }

        var names = new List<string>();
        foreach ((ReadingFlags flag, string name) in Table)
        {
            if (flags.HasFlag(flag))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Returns the flag for a vocabulary string; the match is ordinal and case-sensitive.</summary>
    /// <param name="name">The vocabulary string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">The string is not in the vocabulary; the message echoes at most 64 characters of it with control characters replaced.</exception>
    public static ReadingFlags Parse(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return TryParse(name, out ReadingFlags flag)
            ? flag
            : throw new ArgumentException($"Unknown reading flag \"{TextEcho.ForMessage(name)}\".", nameof(name));
    }

    /// <summary>Tries to return the flag for a vocabulary string; the match is ordinal and case-sensitive and a null string is simply unknown.</summary>
    /// <param name="name">The vocabulary string.</param>
    /// <param name="flag">The flag, or <see cref="ReadingFlags.None"/> when the string is unknown.</param>
    public static bool TryParse(string? name, out ReadingFlags flag)
    {
        foreach ((ReadingFlags candidate, string candidateName) in Table)
        {
            if (string.Equals(candidateName, name, StringComparison.Ordinal))
            {
                flag = candidate;
                return true;
            }
        }

        flag = ReadingFlags.None;
        return false;
    }
}