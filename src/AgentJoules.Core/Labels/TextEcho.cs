using System.Globalization;

namespace AgentJoules.Core.Labels;

/// <summary>Makes caller- or document-controlled text safe to put into an exception message that may reach a log or a console.</summary>
internal static class TextEcho
{
    private const int MaxLength = 64;

    /// <summary>
    /// Cuts the text to 64 characters (appending <c>...</c> when cut) and replaces every control character (C0, DEL, C1),
    /// format character (for example U+202E) and line or paragraph separator (U+2028, U+2029) with <c>?</c>.
    /// </summary>
    /// <param name="text">The text to echo.</param>
    internal static string ForMessage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Cut one character earlier when the cut would separate a surrogate pair, so no lone high surrogate is left behind.
        int cut = text.Length > MaxLength && char.IsHighSurrogate(text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
        string bounded = text.Length > MaxLength ? string.Concat(text.AsSpan(0, cut), "...") : text;

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