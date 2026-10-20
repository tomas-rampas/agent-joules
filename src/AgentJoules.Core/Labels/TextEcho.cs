using System.Globalization;

namespace AgentJoules.Core.Labels;

/// <summary>Makes caller- or document-controlled text safe to put into an exception message that may reach a log or a console.</summary>
public static class TextEcho
{
    /// <summary>The number of characters an echo keeps unless the caller asks for another length.</summary>
    public const int DefaultMaxLength = 64;

    /// <summary>
    /// Cuts the text to <paramref name="maxLength"/> characters (appending <c>...</c> when cut) and replaces every control character (C0, DEL, C1),
    /// format character (for example U+202E) and line or paragraph separator (U+2028, U+2029) with <c>?</c>.
    /// </summary>
    /// <param name="text">The text to echo.</param>
    /// <param name="maxLength">The most characters of <paramref name="text"/> to keep, at least 1; 64 unless given.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is less than 1.</exception>
    public static string ForMessage(string text, int maxLength = DefaultMaxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        // Cut one character earlier when the cut would separate a surrogate pair, so no lone high surrogate is left behind.
        int cut = text.Length > maxLength && char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        string bounded = text.Length > maxLength ? string.Concat(text.AsSpan(0, cut), "...") : text;

        return string.Create(bounded.Length, bounded, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = IsUnsafe(source[i]) ? '?' : source[i];
            }
        });
    }

    private static bool IsUnsafe(char c) =>
        char.IsControl(c)
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}