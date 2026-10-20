using System.Globalization;

using AgentJoules.Core.Labels;

namespace AgentJoules.Core.Configuration;

/// <summary>Message pieces that the shape check and the validators share, so that one problem reads the same wherever it is found. No piece holds a configured value.</summary>
internal static class ConfigurationMessages
{
    /// <summary>Joins words as <c>a, b or c</c>.</summary>
    /// <param name="words">The words; at least one.</param>
    internal static string OrList(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        return words.Count switch
        {
            0 => string.Empty,
            1 => words[0],
            _ => string.Join(", ", words.Take(words.Count - 1)) + " or " + words[^1],
        };
    }

    // The longest valid segment (Baseline:MaxCoefficientOfVariation's last part) is 25 characters. A longer segment is not echoed, so a
    // misplaced value (a key pasted as a key name) cannot ride into a message as a key name.
    private const int MaxEchoedSegmentLength = 32;

    /// <summary>The text that stands for one key segment in a message: the segment itself when it is at most 32 characters (cleaned of control characters), otherwise only its length.</summary>
    /// <param name="segment">A key segment from the configuration sources, a dictionary key included.</param>
    internal static string Segment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        return segment.Length == 0 ? "(empty key name)"
            : segment.Length > MaxEchoedSegmentLength ? $"a key segment of {segment.Length.ToString(CultureInfo.InvariantCulture)} characters"
            : TextEcho.ForMessage(segment);
    }

    /// <summary>The line for a tier section whose name is not one of <see cref="TierOptions.KnownNames"/>.</summary>
    /// <param name="path">The key path of the section, already safe to print (for example <c>Tiers:bogus</c>).</param>
    internal static string UnknownTier(string path) => $"{path} is not a known tier; use {OrList(TierOptions.KnownNames)}";

    /// <summary>The line for an agent name that is not one of <see cref="TierOptions.KnownAgentNames"/>.</summary>
    /// <param name="path">The key path of the entry, already safe to print.</param>
    internal static string UnknownAgent(string path) => $"{path} is not a known agent; use {OrList(TierOptions.KnownAgentNames)}";
}